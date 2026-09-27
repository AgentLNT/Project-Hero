using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 字段级比较器：把两份 <see cref="LogicSnapshot"/> 逐字段做比较，
    /// 按四类结果输出 <see cref="ShadowFieldDifference"/>。
    ///
    /// 对齐规则：**按逻辑 Tick 配对**，绝不使用 Unity 帧序号或容器枚举顺序
    /// （任务包「必须产出」6）。无法按逻辑 Tick 配对的检查点计入
    /// <see cref="ShadowComparisonReport.UnalignedCheckpoints"/>，不做任何猜测。
    /// </summary>
    public static class ShadowDifferenceDetector
    {
        /// <summary>Legacy 侧显式"不可采样"标记（暂不可比较字段专用）。</summary>
        public const string LegacyNotSampled = "<not-sampled-legacy>";

        public static ShadowComparisonReport Compare(ShadowComparisonInput input, ShadowCasePolicy policy)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            var configuration = input.Configuration;
            var report = new ShadowComparisonReport(
                input.BattleDefinitionHash, input.Encounter, input.Mode, input.InputSummary,
                input.RulesVersion, configuration.ComparisonConfigVersion);

            for (int i = 0; i < policy.TemporarilyUncomparable.Count; i++)
                report.AddTemporarilyUncomparable(policy.TemporarilyUncomparable[i]);
            for (int i = 0; i < policy.Approvals.Count; i++)
                report.AddApproval(policy.Approvals[i]);
            for (int i = 0; i < policy.Rejections.Count; i++)
                report.AddRejection(policy.Rejections[i]);

            var legacy = input.LegacyCheckpoints;
            var shadow = input.ShadowCheckpoints;

            // Legacy 侧只读观测存在时走**真实可比较字段**通道（任务 03B 第二收尾轮 R1）：
            // 旧权威不是一个 Logic 世界，把它塞进 LogicSnapshot 只能靠编造默认值，
            // 那样的"比较"会把未实现字段冒充成已比较。这里改成逐字段的显式可比较集合。
            if (input.LegacyObservations.Count > 0)
            {
                CompareObservedLegacyCheckpoints(report, policy, input);
                return report;
            }

            if (legacy.Count > configuration.MaxStepsPerComparison)
            {
                report.MarkBudgetOverrun(ShadowComparisonCodes.ShadowBudgetOverrun
                    + "|checkpoints=" + legacy.Count
                    + "|budget=" + configuration.MaxStepsPerComparison);
                return report;
            }

            int shadowIndex = 0;
            for (int i = 0; i < legacy.Count; i++)
            {
                var legacySnapshot = legacy[i];
                if (legacySnapshot == null)
                {
                    report.UnalignedCheckpoints++;
                    continue;
                }

                // 按逻辑 Tick 前进；Shadow 侧 Tick 落后时继续找，找不到即记未对齐。
                while (shadowIndex < shadow.Count
                       && shadow[shadowIndex] != null
                       && shadow[shadowIndex].Tick < legacySnapshot.Tick)
                {
                    shadowIndex++;
                }

                if (shadowIndex >= shadow.Count || shadow[shadowIndex] == null)
                {
                    report.UnalignedCheckpoints++;
                    continue;
                }

                if (shadow[shadowIndex].Tick != legacySnapshot.Tick)
                {
                    // 两侧在同一检查点位置上的逻辑 Tick 不同：这是**基础设施事实差异**
                    // （对齐键本身不一致）。必须显式报告并计入未对齐，
                    // 既不静默消费掉这个检查点，也不按帧序号强行配对。
                    long legacyTick = legacySnapshot.Tick;
                    long shadowTick = shadow[shadowIndex].Tick;
                    report.UnalignedCheckpoints++;
                    report.AddDifference(new ShadowFieldDifference(
                        ShadowDifferenceKind.InfrastructureFact, legacyTick, "LateUpdate@" + legacyTick,
                        "checkpoint.tick", legacyTick.ToString(CultureInfo.InvariantCulture),
                        shadowTick.ToString(CultureInfo.InvariantCulture),
                        "同一检查点位置上的逻辑 Tick 不同：两侧无法按逻辑 Tick 对齐", -1));
                    shadowIndex++;
                    continue;
                }

                var shadowSnapshot = shadow[shadowIndex];
                shadowIndex++;
                report.ComparedCheckpoints++;
                CompareTicks(report, policy, legacySnapshot, shadowSnapshot, input.EventSequenceAt(legacySnapshot.Tick));
            }

            if (shadowIndex < shadow.Count) report.UnalignedCheckpoints += shadow.Count - shadowIndex;

            return report;
        }

        private static void CompareTicks(
            ShadowComparisonReport report, ShadowCasePolicy policy,
            LogicSnapshot legacy, LogicSnapshot shadow, long relatedEventSequence)
        {
            string checkpoint = "LateUpdate@" + legacy.Tick.ToString(CultureInfo.InvariantCulture);
            long eventSequence = relatedEventSequence;

            InfrastructureFacts(report, policy, legacy, shadow, checkpoint, eventSequence);
            NewRuleFacts(report, policy, legacy, shadow, checkpoint, eventSequence);
            // 任务 05：排程/计划/Lane/机会的逐条事实。它是**逐用例开启**的
            // （<see cref="ShadowCasePolicy.CompareScheduleFacts"/>，默认关闭 ⇒ 既有用例的报告逐字节不变），
            // 且刻意排在既有的单位事实之后，因此"首个差异"的既有顺序不被改变。
            if (policy.CompareScheduleFacts)
                Task05ScheduleFacts(report, policy, legacy, shadow, checkpoint, eventSequence);
            // 任务 06：LogicGrid 占位 / 移动提交 / 路径 Reservation / 冲突回滚 / 移动终态。
            // 同样**逐用例开启**（默认关闭 ⇒ 既有用例的报告逐字节不变），且刻意排在任务 05 之后，
            // 因此"首个差异"的既有顺序不被改变。
            if (policy.CompareMovementFacts)
                Task06MovementFacts(report, policy, legacy, shadow, checkpoint, eventSequence);
            // 任务 07：TurnWindow / 整数预算 / 并发授权 / 肾上腺素周期 / 跨窗口计划不变性。
            // 同样**逐用例开启**（默认关闭 ⇒ 既有用例的报告逐字节不变），且刻意排在任务 06 之后，
            // 因此"首个差异"的既有顺序不被改变。
            if (policy.CompareTurnWindowFacts)
                Task07TurnWindowFacts(report, policy, legacy, shadow, checkpoint, eventSequence);
            TemporarilyUncomparableFacts(report, policy, legacy, shadow, checkpoint, eventSequence);
        }

        // -------- 任务 05：ActionPlan / ActorLane / 反应机会的逐条事实 --------

        /// <summary>
        /// <strong>任务 05 的 Shadow 检查点扩展</strong>（Logic 世界对 Logic 世界通道）。
        ///
        /// 覆盖范围（与任务包「工作步骤」10 逐项对应）：
        /// <list type="bullet">
        /// <item><strong>排程编辑</strong>：全局 <c>scheduleRevision</c> 与每个计划的
        /// <c>startTick</c>/<c>endTick</c>（成功事务与系统自动延期各 +1，失败/预览/锁定不 +1）；</item>
        /// <item><strong>跨窗口不变</strong>：<c>submittedWindowId</c> 只作为审计字段参与比较，
        /// 不参与执行过滤——它出现在比较里正是为了证明"窗口归属不改变计划事实"；</item>
        /// <item><strong>门禁与延期</strong>：<c>automaticDeferralCount</c> 与 <c>lockedAtTick</c>；</item>
        /// <item><strong>原子启动</strong>：<c>state</c> + <c>lockedAtTick</c> 必须同时一致
        /// （不存在"一侧 Locked 未 Running"的合法差异）；</item>
        /// <item><strong>Lane 串行</strong>：逐 Lane 的 <c>pendingPlanCount</c> 与提交锁；</item>
        /// <item><strong>Impact 与 Recovery</strong>：<c>impactTick</c>/<c>recoveryTicks</c>/<c>resolvedWindupTicks</c>；</item>
        /// <item><strong>全部终态</strong>：<c>terminationReason</c>/<c>terminalTick</c> 与
        /// 终态历史摘要 <c>terminalPlanRecordCount</c>；</item>
        /// <item><strong>反应机会</strong>：逐机会 <c>state</c>/<c>triggerTick</c>/<c>boundActionPlanId</c>
        /// 与 <c>nextReactionOpportunityId</c>。</item>
        /// </list>
        ///
        /// 差异一律按<strong>具体字段路径</strong>登记；全部为
        /// <see cref="ShadowDifferenceKind.NewRuleVerifiedFact"/>（差异即回归），
        /// 并先经过逐用例 + RulesVersion 的批准差异匹配（本任务<strong>不批准任何差异</strong>）。
        /// </summary>
        private static void Task05ScheduleFacts(
            ShadowComparisonReport report, ShadowCasePolicy policy,
            LogicSnapshot legacy, LogicSnapshot shadow, string checkpoint, long eventSequence)
        {
            long tick = legacy != null ? legacy.Tick : (shadow != null ? shadow.Tick : -1L);

            AddNewRuleFact(report, policy, tick, checkpoint, "scheduleRevision",
                legacy.ScheduleRevision, shadow.ScheduleRevision,
                "全局排程修订号必须一致：成功命令事务与成功系统自动延期各 +1，失败/预览/锁定/自然推进不 +1（任务 05）",
                eventSequence);

            // —— 逐计划：按 ActionPlanId 升序（稳定键），两侧取并集，缺项即差异 ——
            var legacyPlans = PlansById(legacy);
            var shadowPlans = PlansById(shadow);
            foreach (long id in UnionOfKeys(legacyPlans, shadowPlans))
            {
                string prefix = "plans[" + id.ToString(CultureInfo.InvariantCulture) + "]";
                bool hasLegacy = legacyPlans.TryGetValue(id, out ActionPlanSnapshot legacyPlan);
                bool hasShadow = shadowPlans.TryGetValue(id, out ActionPlanSnapshot shadowPlan);

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".present",
                    hasLegacy, hasShadow, "活动计划集合必须一致（任务 05：终态立即离开活动索引）", eventSequence);
                if (!hasLegacy || !hasShadow) continue;

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".actionSpecId",
                    legacyPlan.ActionSpecId, shadowPlan.ActionSpecId, "计划身份必须一致（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".state",
                    legacyPlan.State, shadowPlan.State,
                    "生命周期状态 Editable/Locked/Running/Completed/Terminated 必须一致（任务 05 原子启动）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".startTick",
                    legacyPlan.StartTick, shadowPlan.StartTick, "排程编辑与系统延期后的起点必须一致（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".endTick",
                    legacyPlan.EndTick, shadowPlan.EndTick, "投影终点必须与起点同源重绑（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".lastRequestedStartTick",
                    legacyPlan.LastRequestedStartTick, shadowPlan.LastRequestedStartTick,
                    "请求起点只由直接 Move 更新，系统自动延期不得覆盖（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".lockedAtTick",
                    legacyPlan.LockedAtTick, shadowPlan.LockedAtTick,
                    "锁定 Tick 与 Running 必须同一次原子提交写入（任务 05 门禁）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".automaticDeferralCount",
                    legacyPlan.AutomaticDeferralCount, shadowPlan.AutomaticDeferralCount,
                    "系统自动延期次数必须一致（任务 05 门禁与延期）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".impactTick",
                    legacyPlan.ImpactTick, shadowPlan.ImpactTick, "ImpactTick 必须等于解析前摇终点（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".resolvedWindupTicks",
                    legacyPlan.ResolvedWindupTicks, shadowPlan.ResolvedWindupTicks,
                    "前摇只在首次进入 Editable 时解析一次（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".recoveryTicks",
                    legacyPlan.RecoveryTicks, shadowPlan.RecoveryTicks,
                    "后摇来自 TimingSpec，不随速度改变（任务 05 Impact/Recovery）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".submittedWindowId",
                    legacyPlan.SubmittedWindowId, shadowPlan.SubmittedWindowId,
                    "窗口归属只是审计字段：它必须一致，但绝不参与执行过滤（任务 05 跨窗口不变）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".terminationReason",
                    legacyPlan.TerminationReason, shadowPlan.TerminationReason,
                    "终态原因必须由同一个协调器写入（任务 05 全部终态）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".terminalTick",
                    legacyPlan.TerminalTick, shadowPlan.TerminalTick, "TerminalTick 必须一致（任务 05 全部终态）", eventSequence);
            }

            // —— 逐 Lane：按 UnitId 升序 ——
            var legacyLanes = LanesById(legacy);
            var shadowLanes = LanesById(shadow);
            foreach (long unitId in UnionOfKeys(legacyLanes, shadowLanes))
            {
                string prefix = "actorLanes[" + unitId.ToString(CultureInfo.InvariantCulture) + "]";
                bool hasLegacy = legacyLanes.TryGetValue(unitId, out ActorLaneSnapshot legacyLane);
                bool hasShadow = shadowLanes.TryGetValue(unitId, out ActorLaneSnapshot shadowLane);

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".present",
                    hasLegacy, hasShadow, "Lane 集合必须一致（任务 05）", eventSequence);
                if (!hasLegacy || !hasShadow) continue;

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".pendingPlanCount",
                    legacyLane.PendingPlanCount, shadowLane.PendingPlanCount,
                    "Lane 串行：同一单位的队列内容必须一致（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".locked",
                    legacyLane.Locked, shadowLane.Locked,
                    "Lane 提交锁必须一致（死亡/战斗结束只锁新提交，不清除已有计划）", eventSequence);
            }

            // —— 反应机会：数量 + 逐条状态 + 下一个 ID ——
            AddNewRuleFact(report, policy, tick, checkpoint, "reactionOpportunities.count",
                legacy.ReactionOpportunities.Count, shadow.ReactionOpportunities.Count,
                "机会集合必须一致（任务 05 机会状态机）", eventSequence);

            var legacyOpportunities = OpportunitiesById(legacy);
            var shadowOpportunities = OpportunitiesById(shadow);
            foreach (long id in UnionOfKeys(legacyOpportunities, shadowOpportunities))
            {
                string prefix = "reactionOpportunities[" + id.ToString(CultureInfo.InvariantCulture) + "]";
                bool hasLegacy = legacyOpportunities.TryGetValue(id, out ReactionOpportunitySnapshot legacyOpportunity);
                bool hasShadow = shadowOpportunities.TryGetValue(id, out ReactionOpportunitySnapshot shadowOpportunity);

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".present",
                    hasLegacy, hasShadow, "机会集合必须一致（任务 05）", eventSequence);
                if (!hasLegacy || !hasShadow) continue;

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".state",
                    legacyOpportunity.State, shadowOpportunity.State,
                    "机会状态 Open/Accepted/Triggered/Expired/SourceCancelled/BattleEnded 必须一致（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".triggerTick",
                    legacyOpportunity.TriggerTick, shadowOpportunity.TriggerTick,
                    "TriggerTick 恒等于来源攻击 ImpactTick（任务 05，命令不可声明）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".telegraphTick",
                    legacyOpportunity.TelegraphTick, shadowOpportunity.TelegraphTick,
                    "TelegraphTick 必须是原子启动之后的 Tick（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".boundActionPlanId",
                    legacyOpportunity.BoundActionPlanId, shadowOpportunity.BoundActionPlanId,
                    "机会与绑定反应计划必须一一对应（任务 05）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".closeReason",
                    legacyOpportunity.CloseReason, shadowOpportunity.CloseReason,
                    "关闭原因必须一致（任务 05：第一次离开 Open 只发一次关闭事件）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".openOptionCount",
                    legacyOpportunity.OpenOptionCount, shadowOpportunity.OpenOptionCount,
                    "开放选项数必须一致（任务 05 逐选项截止/过期）", eventSequence);
            }

            AddNewRuleFact(report, policy, tick, checkpoint, "nextReactionOpportunityId",
                legacy.NextReactionOpportunityId, shadow.NextReactionOpportunityId,
                "下一个机会 ID 必须来自同一分配器（任务 05）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "nextReactionOptionSequence",
                legacy.NextReactionOptionSequence, shadow.NextReactionOptionSequence,
                "机会序号必须一致（任务 05）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "terminalPlanRecordCount",
                legacy.TerminalPlanRecordCount, shadow.TerminalPlanRecordCount,
                "终态计划历史记录数必须一致（任务 05 归档摘要）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "terminalPlanDigest",
                legacy.TerminalPlanDigest, shadow.TerminalPlanDigest,
                "终态计划历史摘要必须一致（任务 05 归档摘要）", eventSequence);
        }

        private static Dictionary<long, ActionPlanSnapshot> PlansById(LogicSnapshot snapshot)
        {
            var map = new Dictionary<long, ActionPlanSnapshot>();
            if (snapshot == null || snapshot.Plans == null) return map;
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                ActionPlanSnapshot plan = snapshot.Plans[i];
                if (plan != null) map[plan.ActionPlanId] = plan;
            }
            return map;
        }

        private static Dictionary<long, ActorLaneSnapshot> LanesById(LogicSnapshot snapshot)
        {
            var map = new Dictionary<long, ActorLaneSnapshot>();
            if (snapshot == null || snapshot.ActorLanes == null) return map;
            for (int i = 0; i < snapshot.ActorLanes.Count; i++)
            {
                ActorLaneSnapshot lane = snapshot.ActorLanes[i];
                if (lane != null) map[lane.UnitId] = lane;
            }
            return map;
        }

        private static Dictionary<long, ReactionOpportunitySnapshot> OpportunitiesById(LogicSnapshot snapshot)
        {
            var map = new Dictionary<long, ReactionOpportunitySnapshot>();
            if (snapshot == null || snapshot.ReactionOpportunities == null) return map;
            for (int i = 0; i < snapshot.ReactionOpportunities.Count; i++)
            {
                ReactionOpportunitySnapshot opportunity = snapshot.ReactionOpportunities[i];
                if (opportunity != null) map[opportunity.ReactionOpportunityId] = opportunity;
            }
            return map;
        }

        /// <summary>两侧键的并集，按数值升序（稳定键 ⇒ 稳定差异顺序）。</summary>
        private static List<long> UnionOfKeys<T>(Dictionary<long, T> left, Dictionary<long, T> right)
        {
            var keys = new List<long>(left.Count + right.Count);
            var seen = new HashSet<long>();
            foreach (long key in left.Keys)
            {
                if (seen.Add(key)) keys.Add(key);
            }
            foreach (long key in right.Keys)
            {
                if (seen.Add(key)) keys.Add(key);
            }
            keys.Sort();
            return keys;
        }

        // -------- 任务 06：LogicGrid 占位 / 移动提交 / 路径 Reservation / 冲突回滚 / 移动终态 --------

        /// <summary>
        /// 规范格事实（只携带整数坐标）。
        ///
        /// 刻意<strong>不</strong>复用 <c>GridPoint</c>：它在构造期校验 doubled-coordinate 奇偶并以
        /// 稳定码抛错，而比较器必须在快照被篡改/损坏时<strong>报告差异</strong>，不能抛异常——
        /// 否则"两侧不一致"会退化成测试基础设施错误而不是一条差异。
        /// </summary>
        private readonly struct CellFact : IEquatable<CellFact>, IComparable<CellFact>
        {
            public CellFact(int x, int y)
            {
                X = x;
                Y = y;
            }

            public int X { get; }

            public int Y { get; }

            public bool Equals(CellFact other) => X == other.X && Y == other.Y;

            public override bool Equals(object obj) => obj is CellFact other && Equals(other);

            public override int GetHashCode() => (X * 397) ^ Y;

            public int CompareTo(CellFact other)
            {
                int byX = X.CompareTo(other.X);
                return byX != 0 ? byX : Y.CompareTo(other.Y);
            }

            public override string ToString()
                => X.ToString(CultureInfo.InvariantCulture) + "," + Y.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 移动段的规范定位键 <c>(ActionPlanId, StepIndex)</c>——与 Logic 侧
        /// <c>MovementSegment</c>/<c>ReservationKey</c> 的冻结定位键逐字一致（没有独立 SegmentId）。
        /// </summary>
        private readonly struct MovementSegmentKey : IEquatable<MovementSegmentKey>, IComparable<MovementSegmentKey>
        {
            public MovementSegmentKey(long actionPlanId, int stepIndex)
            {
                ActionPlanId = actionPlanId;
                StepIndex = stepIndex;
            }

            public long ActionPlanId { get; }

            public int StepIndex { get; }

            public bool Equals(MovementSegmentKey other)
                => ActionPlanId == other.ActionPlanId && StepIndex == other.StepIndex;

            public override bool Equals(object obj) => obj is MovementSegmentKey other && Equals(other);

            public override int GetHashCode() => (ActionPlanId * 397L ^ StepIndex).GetHashCode();

            public int CompareTo(MovementSegmentKey other)
            {
                int byPlan = ActionPlanId.CompareTo(other.ActionPlanId);
                return byPlan != 0 ? byPlan : StepIndex.CompareTo(other.StepIndex);
            }

            public override string ToString()
                => ActionPlanId.ToString(CultureInfo.InvariantCulture) + "/"
                   + StepIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 某计划的"移动提交"事实：未提交段数 + 第一条未提交段的 <c>From</c> 与该计划拥有者当前锚点的关系。
        /// </summary>
        private readonly struct MovementCommitFact
        {
            public MovementCommitFact(int pendingSegments, string expectedFrom)
            {
                PendingSegments = pendingSegments;
                ExpectedFrom = expectedFrom;
            }

            public int PendingSegments { get; }

            public string ExpectedFrom { get; }
        }

        /// <summary>已离开活动计划索引的 <c>ActionPlanId</c> 仍残留的移动产物（"移动终态"事实）。</summary>
        private sealed class TerminalArtifactFacts
        {
            public readonly Dictionary<long, int> SegmentsByPlan = new Dictionary<long, int>();

            public readonly Dictionary<long, int> ReservationsByPlan = new Dictionary<long, int>();

            public int TotalSegments;

            public int TotalReservations;
        }

        /// <summary>共享的空格集合（只读使用：所有写入都发生在本方法内新建的列表上）。</summary>
        private static readonly List<CellFact> EmptyCells = new List<CellFact>();

        /// <summary>
        /// <strong>任务 06 的 Shadow 检查点扩展</strong>（Logic 世界对 Logic 世界通道）。
        ///
        /// 覆盖任务包「必须产出」10 的五类逻辑空间事实：
        /// <list type="number">
        /// <item><strong>占位</strong>：逐 <c>UnitId</c> 的逻辑格锚点（规范化快照的 <c>units[i].X/Y</c>
        /// 就是 <c>LogicGrid</c> 锚点经命令前边界同步后的权威值），以及该锚点上是否仍残留 Reservation；</item>
        /// <item><strong>移动提交</strong>：逐段的 <c>From/To/EndTick</c>，加上"每个持有未提交段的计划，
        /// 其<strong>第一条未提交段的 From 必须等于该计划拥有者的当前锚点</strong>"——即命令前边界提交后的
        /// 逻辑位置恰好是剩余路径的起点；</item>
        /// <item><strong>路径 Reservation</strong>：逐计划的规范格集合、数量，以及"每条段的 To 都有同计划的
        /// Reservation、且数量相等"这一 1:1 覆盖关系；</item>
        /// <item><strong>冲突回滚</strong>：以<strong>状态</strong>口径覆盖——被拒绝的候选批次不得在段的
        /// <c>(ActionPlanId, StepIndex)</c> 集合、段的 <c>From/To/EndTick</c> 或计划的 Reservation 格集合里
        /// 留下任何痕迹。快照是状态模型，"失败后零写入"的<strong>时序面</strong>由用例直接断言真实
        /// <c>LogicGridMovementAuthority</c> 在替换前/后的状态（同一事务的零局部写入）；</item>
        /// <item><strong>移动终态</strong>：任何<strong>已离开活动计划索引</strong>的 <c>ActionPlanId</c>
        /// 都不得再出现在段表或 Reservation 表里（逐计划计数 + 合计），因此"原 EndTick 再提交一次"
        /// 在结构上不可能。</item>
        /// </list>
        ///
        /// <strong>比较面边界</strong>（任务包「必须产出」10 末句）：全部事实只来自
        /// <see cref="LogicSnapshot"/> 的<strong>整数</strong>字段（单位锚点、段与 Reservation 的整数坐标与
        /// EndTick、计划的拥有者）。本类没有任何 UnityEngine 类型的成员，也拿不到 Transform、
        /// 视觉插值进度或 Unity 帧序号，因此这里比较的"位置"只可能是逻辑格。
        ///
        /// 差异一律按精确字段路径登记为 <see cref="ShadowDifferenceKind.NewRuleVerifiedFact"/>
        /// （差异即回归），并先经过逐用例 + RulesVersion 的批准差异匹配；本任务<strong>不批准</strong>任何差异。
        /// 事实分组顺序固定（占位 → 段 → Reservation → 提交 → 终态），因此"首个差异"是稳定的。
        /// </summary>
        private static void Task06MovementFacts(
            ShadowComparisonReport report, ShadowCasePolicy policy,
            LogicSnapshot legacy, LogicSnapshot shadow, string checkpoint, long eventSequence)
        {
            long tick = legacy != null ? legacy.Tick : (shadow != null ? shadow.Tick : -1L);

            var legacyAnchors = AnchorsByUnit(legacy);
            var shadowAnchors = AnchorsByUnit(shadow);
            var legacySegments = SegmentsByKey(legacy);
            var shadowSegments = SegmentsByKey(shadow);
            var legacyReservations = ReservationsByPlan(legacy);
            var shadowReservations = ReservationsByPlan(shadow);

            // —— ① 占位：逐单位逻辑格锚点 + 已提交格上的 Reservation 残留 ——
            List<long> unitIds = UnionOfKeys(legacyAnchors, shadowAnchors);
            for (int i = 0; i < unitIds.Count; i++)
            {
                long unitId = unitIds[i];
                string prefix = "occupancy[" + unitId.ToString(CultureInfo.InvariantCulture) + "]";
                bool hasLegacy = legacyAnchors.TryGetValue(unitId, out CellFact legacyAnchor);
                bool hasShadow = shadowAnchors.TryGetValue(unitId, out CellFact shadowAnchor);

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".present",
                    hasLegacy, hasShadow,
                    "占位主体集合必须一致（任务 06：LogicGrid 已注册单位，逐 UnitId 稳定键）", eventSequence);
                if (!hasLegacy || !hasShadow) continue;

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".anchor",
                    legacyAnchor, shadowAnchor,
                    "单位逻辑格锚点必须一致：它是 LogicGrid 的占位权威，命令前边界提交后与段的目的格同步（任务 06）",
                    eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".anchorReservations",
                    CountReservationsAt(legacyReservations, legacyAnchor),
                    CountReservationsAt(shadowReservations, shadowAnchor),
                    "已提交逻辑格的占位与释放必须同一次原子完成：提交后该格不得仍被他人 Reservation 持有（任务 06）",
                    eventSequence);
            }

            // —— ② 移动提交：逐段存在性 + 段的整数事实 ——
            List<MovementSegmentKey> segmentKeys = UnionSegmentKeys(legacySegments, shadowSegments);
            for (int i = 0; i < segmentKeys.Count; i++)
            {
                MovementSegmentKey key = segmentKeys[i];
                string prefix = "movementSegments[" + key + "]";
                bool hasLegacy = legacySegments.TryGetValue(key, out MovementSegmentSnapshot legacySegment);
                bool hasShadow = shadowSegments.TryGetValue(key, out MovementSegmentSnapshot shadowSegment);

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".present",
                    hasLegacy, hasShadow,
                    "活动/未来移动段集合必须一致（键 = (ActionPlanId, StepIndex)，与 Logic 的冻结定位键一致）",
                    eventSequence);
                if (!hasLegacy || !hasShadow) continue;

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".from",
                    new CellFact(legacySegment.FromX, legacySegment.FromY),
                    new CellFact(shadowSegment.FromX, shadowSegment.FromY),
                    "段的起点格必须一致：StartTick <= tick < EndTick 期间单位仍占该格（任务 06 离散提交）",
                    eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".to",
                    new CellFact(legacySegment.ToX, legacySegment.ToY),
                    new CellFact(shadowSegment.ToX, shadowSegment.ToY),
                    "段的目的格必须一致：它是 EndTick 命令前边界的原子提交目标（任务 06）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".endTick",
                    legacySegment.EndTick, shadowSegment.EndTick,
                    "段的结束边界必须一致：提交时机只由段区间与 Step 阶段顺序决定，与视觉插值进度无关（任务 06）",
                    eventSequence);
            }

            // —— ③ 路径 Reservation：逐计划规范格集合 + 与段的 1:1 覆盖关系 ——
            List<long> reservationPlans = UnionOfKeys(legacyReservations, shadowReservations);
            for (int i = 0; i < reservationPlans.Count; i++)
            {
                long planId = reservationPlans[i];
                string prefix = "reservations[" + planId.ToString(CultureInfo.InvariantCulture) + "]";
                List<CellFact> legacyCells;
                if (!legacyReservations.TryGetValue(planId, out legacyCells)) legacyCells = EmptyCells;
                List<CellFact> shadowCells;
                if (!shadowReservations.TryGetValue(planId, out shadowCells)) shadowCells = EmptyCells;

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".count",
                    legacyCells.Count, shadowCells.Count,
                    "每个计划持有的 Reservation 数必须一致（一格一持有者，先成功提交者持有，任务 06）",
                    eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".cells",
                    DescribeCells(legacyCells), DescribeCells(shadowCells),
                    "Reservation 的规范格集合必须一致（按 (X, Y) 升序，与容器枚举顺序无关，任务 06）",
                    eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".matchesSegmentTos",
                    ReservationsMatchSegmentTos(legacySegments, planId, legacyCells),
                    ReservationsMatchSegmentTos(shadowSegments, planId, shadowCells),
                    "每条段的 To 必须有同计划的 Reservation 且数量相等（路径预留与段一一对应，任务 06）",
                    eventSequence);
            }

            // —— ②/④ 移动提交后的逻辑位置：剩余段数 + 第一条未提交段的 From vs 计划拥有者锚点 ——
            Dictionary<long, MovementCommitFact> legacyCommits = CommitFacts(legacy);
            Dictionary<long, MovementCommitFact> shadowCommits = CommitFacts(shadow);
            List<long> commitPlans = UnionOfKeys(legacyCommits, shadowCommits);
            for (int i = 0; i < commitPlans.Count; i++)
            {
                long planId = commitPlans[i];
                string prefix = "movementCommit[" + planId.ToString(CultureInfo.InvariantCulture) + "]";
                MovementCommitFact legacyCommit;
                if (!legacyCommits.TryGetValue(planId, out legacyCommit))
                    legacyCommit = new MovementCommitFact(0, "<none>");
                MovementCommitFact shadowCommit;
                if (!shadowCommits.TryGetValue(planId, out shadowCommit))
                    shadowCommit = new MovementCommitFact(0, "<none>");

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".pendingSegments",
                    legacyCommit.PendingSegments, shadowCommit.PendingSegments,
                    "计划的未提交段数必须一致：命令前边界按 (EndTick, ActionPlanId, StepIndex) 恰好移除到期段（任务 06）",
                    eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".expectedFrom",
                    legacyCommit.ExpectedFrom, shadowCommit.ExpectedFrom,
                    "移动提交后的逻辑位置必须是剩余路径的起点：第一条未提交段的 From 必须等于该计划拥有者的当前锚点（任务 06）",
                    eventSequence);
            }

            // —— ⑤ 移动终态：离开活动计划索引的 ActionPlanId 不得残留段/预留 ——
            TerminalArtifactFacts legacyArtifacts = TerminalArtifacts(legacy);
            TerminalArtifactFacts shadowArtifacts = TerminalArtifacts(shadow);
            AddTerminalArtifactFacts(report, policy, tick, checkpoint, eventSequence,
                legacyArtifacts.SegmentsByPlan, shadowArtifacts.SegmentsByPlan, ".segments",
                "计划进入终态后不得存在该计划的活动/未来移动段（原 EndTick 不得再提交，任务 06）");
            AddTerminalArtifactFacts(report, policy, tick, checkpoint, eventSequence,
                legacyArtifacts.ReservationsByPlan, shadowArtifacts.ReservationsByPlan, ".reservations",
                "计划进入终态后必须释放该计划的全部 Reservation（按稳定空间键，任务 06）");
            AddNewRuleFact(report, policy, tick, checkpoint, "movementTerminal.orphanSegments",
                legacyArtifacts.TotalSegments, shadowArtifacts.TotalSegments,
                "离开活动计划索引的计划持有的段总数必须一致且为零（任务 06 移动终态）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "movementTerminal.orphanReservations",
                legacyArtifacts.TotalReservations, shadowArtifacts.TotalReservations,
                "离开活动计划索引的计划持有的 Reservation 总数必须一致且为零（任务 06 移动终态）", eventSequence);
        }

        private static void AddTerminalArtifactFacts(
            ShadowComparisonReport report, ShadowCasePolicy policy, long tick, string checkpoint,
            long eventSequence,
            Dictionary<long, int> legacyByPlan, Dictionary<long, int> shadowByPlan,
            string suffix, string reason)
        {
            List<long> planIds = UnionOfKeys(legacyByPlan, shadowByPlan);
            for (int i = 0; i < planIds.Count; i++)
            {
                long planId = planIds[i];
                int legacyCount;
                if (!legacyByPlan.TryGetValue(planId, out legacyCount)) legacyCount = 0;
                int shadowCount;
                if (!shadowByPlan.TryGetValue(planId, out shadowCount)) shadowCount = 0;

                AddNewRuleFact(report, policy, tick, checkpoint,
                    "movementTerminal[" + planId.ToString(CultureInfo.InvariantCulture) + "]" + suffix,
                    legacyCount, shadowCount, reason, eventSequence);
            }
        }

        /// <summary>逐 <c>UnitId</c> 的规范化锚点（<c>units[i].X/Y</c> 就是 LogicGrid 锚点）。</summary>
        private static Dictionary<long, CellFact> AnchorsByUnit(LogicSnapshot snapshot)
        {
            var map = new Dictionary<long, CellFact>();
            if (snapshot == null || snapshot.Units == null) return map;
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                UnitSnapshot unit = snapshot.Units[i];
                if (unit == null) continue;
                map[unit.UnitId] = new CellFact(unit.X, unit.Y);
            }
            return map;
        }

        /// <summary>活动计划索引：(ActionPlanId → OwnerUnitId)。</summary>
        private static Dictionary<long, long> PlanOwners(LogicSnapshot snapshot)
        {
            var map = new Dictionary<long, long>();
            if (snapshot == null || snapshot.Plans == null) return map;
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                ActionPlanSnapshot plan = snapshot.Plans[i];
                if (plan != null) map[plan.ActionPlanId] = plan.OwnerUnitId;
            }
            return map;
        }

        private static Dictionary<MovementSegmentKey, MovementSegmentSnapshot> SegmentsByKey(LogicSnapshot snapshot)
        {
            var map = new Dictionary<MovementSegmentKey, MovementSegmentSnapshot>();
            if (snapshot == null || snapshot.MovementSegments == null) return map;
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                if (segment == null) continue;
                map[new MovementSegmentKey(segment.ActionPlanId, segment.StepIndex)] = segment;
            }
            return map;
        }

        /// <summary>两侧段键的并集，按 <c>(ActionPlanId, StepIndex)</c> 升序（稳定差异顺序）。</summary>
        private static List<MovementSegmentKey> UnionSegmentKeys(
            Dictionary<MovementSegmentKey, MovementSegmentSnapshot> left,
            Dictionary<MovementSegmentKey, MovementSegmentSnapshot> right)
        {
            var keys = new SortedSet<MovementSegmentKey>();
            foreach (MovementSegmentKey key in left.Keys) keys.Add(key);
            foreach (MovementSegmentKey key in right.Keys) keys.Add(key);
            return new List<MovementSegmentKey>(keys);
        }

        /// <summary>逐计划的 Reservation 格集合（按 <c>(X, Y)</c> 升序 ⇒ 与容器枚举顺序无关）。</summary>
        private static Dictionary<long, List<CellFact>> ReservationsByPlan(LogicSnapshot snapshot)
        {
            var map = new Dictionary<long, List<CellFact>>();
            if (snapshot == null || snapshot.Reservations == null) return map;
            for (int i = 0; i < snapshot.Reservations.Count; i++)
            {
                ReservationSnapshot reservation = snapshot.Reservations[i];
                if (reservation == null) continue;
                List<CellFact> cells;
                if (!map.TryGetValue(reservation.ActionPlanId, out cells))
                {
                    cells = new List<CellFact>();
                    map[reservation.ActionPlanId] = cells;
                }
                cells.Add(new CellFact(reservation.X, reservation.Y));
            }
            foreach (KeyValuePair<long, List<CellFact>> pair in map) pair.Value.Sort();
            return map;
        }

        private static int CountReservationsAt(Dictionary<long, List<CellFact>> reservationsByPlan, CellFact cell)
        {
            int count = 0;
            foreach (KeyValuePair<long, List<CellFact>> pair in reservationsByPlan)
            {
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    if (pair.Value[i].Equals(cell)) count++;
                }
            }
            return count;
        }

        private static string DescribeCells(List<CellFact> cells)
        {
            if (cells == null || cells.Count == 0) return "<none>";
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) builder.Append(';');
                builder.Append(cells[i]);
            }
            return builder.ToString();
        }

        /// <summary>该计划的段 To 集合与 Reservation 格集合是否一一对应（数量相等且集合相同）。</summary>
        private static bool ReservationsMatchSegmentTos(
            Dictionary<MovementSegmentKey, MovementSegmentSnapshot> segments, long planId, List<CellFact> cells)
        {
            var tos = new SortedSet<CellFact>();
            foreach (KeyValuePair<MovementSegmentKey, MovementSegmentSnapshot> pair in segments)
            {
                if (pair.Key.ActionPlanId != planId) continue;
                tos.Add(new CellFact(pair.Value.ToX, pair.Value.ToY));
            }

            var owned = new SortedSet<CellFact>();
            if (cells != null)
            {
                for (int i = 0; i < cells.Count; i++) owned.Add(cells[i]);
            }

            return tos.Count == (cells != null ? cells.Count : 0) && tos.SetEquals(owned);
        }

        /// <summary>
        /// 逐计划的移动提交事实。段的规范化快照<strong>不</strong>携带 <c>UnitId</c>，
        /// 因此"段属于哪个单位"只能来自活动计划索引的 <c>OwnerUnitId</c>；
        /// 计划/单位缺席时以显式标记表示（不猜、不当作 0）。
        /// </summary>
        private static Dictionary<long, MovementCommitFact> CommitFacts(LogicSnapshot snapshot)
        {
            var facts = new Dictionary<long, MovementCommitFact>();
            if (snapshot == null || snapshot.MovementSegments == null) return facts;

            Dictionary<long, CellFact> anchors = AnchorsByUnit(snapshot);
            Dictionary<long, long> owners = PlanOwners(snapshot);
            var counts = new Dictionary<long, int>();
            var firsts = new Dictionary<long, MovementSegmentSnapshot>();

            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                if (segment == null) continue;

                int count;
                counts.TryGetValue(segment.ActionPlanId, out count);
                counts[segment.ActionPlanId] = count + 1;

                MovementSegmentSnapshot existing;
                if (!firsts.TryGetValue(segment.ActionPlanId, out existing)
                    || segment.StepIndex < existing.StepIndex)
                {
                    firsts[segment.ActionPlanId] = segment;
                }
            }

            foreach (KeyValuePair<long, int> pair in counts)
            {
                MovementSegmentSnapshot first = firsts[pair.Key];
                long owner;
                CellFact anchor;
                string expected;
                if (!owners.TryGetValue(pair.Key, out owner))
                {
                    expected = "<plan-absent>";
                }
                else if (!anchors.TryGetValue(owner, out anchor))
                {
                    expected = "<unit-absent>";
                }
                else
                {
                    var from = new CellFact(first.FromX, first.FromY);
                    expected = from.Equals(anchor)
                        ? "from=" + from
                        : "mismatch:from=" + from + ";anchor=" + anchor;
                }
                facts[pair.Key] = new MovementCommitFact(pair.Value, expected);
            }

            return facts;
        }

        /// <summary>离开活动计划索引的 <c>ActionPlanId</c> 仍残留的段/Reservation 计数。</summary>
        private static TerminalArtifactFacts TerminalArtifacts(LogicSnapshot snapshot)
        {
            var facts = new TerminalArtifactFacts();
            if (snapshot == null) return facts;

            Dictionary<long, long> activePlans = PlanOwners(snapshot);
            if (snapshot.MovementSegments != null)
            {
                for (int i = 0; i < snapshot.MovementSegments.Count; i++)
                {
                    MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                    if (segment == null || activePlans.ContainsKey(segment.ActionPlanId)) continue;
                    int count;
                    facts.SegmentsByPlan.TryGetValue(segment.ActionPlanId, out count);
                    facts.SegmentsByPlan[segment.ActionPlanId] = count + 1;
                    facts.TotalSegments++;
                }
            }

            if (snapshot.Reservations != null)
            {
                for (int i = 0; i < snapshot.Reservations.Count; i++)
                {
                    ReservationSnapshot reservation = snapshot.Reservations[i];
                    if (reservation == null || activePlans.ContainsKey(reservation.ActionPlanId)) continue;
                    int count;
                    facts.ReservationsByPlan.TryGetValue(reservation.ActionPlanId, out count);
                    facts.ReservationsByPlan[reservation.ActionPlanId] = count + 1;
                    facts.TotalReservations++;
                }
            }

            return facts;
        }

        // -------- 任务 07：TurnWindow / 整数预算 / 并发授权 / 肾上腺素周期 --------

        /// <summary>
        /// <strong>任务 07 的 Shadow 检查点扩展</strong>（Logic 世界对 Logic 世界通道）。
        ///
        /// 覆盖任务包「必须产出」11 的五类事实：
        /// <list type="number">
        /// <item><strong>窗口打开/关闭</strong>：<c>currentWindowId</c>/<c>nextWindowTick</c>/
        /// <c>nextWindowOrdinal</c>/<c>lastClosedWindowId</c>/<c>windows.count</c> 与逐窗口的
        /// <c>ownerUnitId</c>/<c>openedAtTick</c>/<c>isOpen</c>/<c>isAcceptingSubmissions</c>/
        /// <c>closeReason</c>；"请求关闭立即停止接受提交、正式关闭在 Tick 末"因此体现为
        /// <b>IsAcceptingSubmissions 与 IsOpen 两个独立的位</b>，不会被合并成一个布尔；</item>
        /// <item><strong>整数预算</strong>：逐窗口 <c>total/reserved/spent/available</c> 四项
        /// （全部为整数 Tick），加上三条<strong>派生不变量</strong>——
        /// <c>windows[i].budgetIdentity</c>（<c>Reserved + Spent + Available == Total</c> 且无负值）、
        /// <c>windows[i].reservationSumMatchesReserved</c>（按计划归属的预留明细合计 == <c>Reserved</c>）、
        /// <c>resources.turnBudgetIdentity</c>（全部窗口聚合，含已关闭窗口的审计账本）；</item>
        /// <item><strong>并发授权</strong>：<c>concurrentAction.hasActiveAuthorization/windowId/playerUnitId</c>、
        /// <c>resources.metaResource</c>（权威费用只被消费一次的证据），以及派生不变量
        /// <c>concurrentAction.authorizationTargetsOpenWindow</c>（授权只能指向仍开放且仍在接受提交的
        /// 当前窗口，且单位不是窗口拥有者本人）；</item>
        /// <item><strong>肾上腺素清零</strong>：逐单位 <c>adrenaline[unitId].available/cycleId/
        /// reservedTotal/reservations</c>，加上单位只读镜像一致性
        /// <c>adrenaline[unitId].mirrorMatchesLedger</c>——"自己窗口打开时先递增周期再清零、
        /// 窗口关闭与跨其他单位窗口都不清零"因此可在逐检查点上被证伪；</item>
        /// <item><strong>跨窗口计划不变性</strong>：逐计划的 <c>budgetCostTicks</c>/
        /// <c>reservedTurnBudgetTicks</c>/<c>submittedWindowLedger</c>/<c>budgetLedgerLinked</c>，
        /// 即"计划在窗口切换前后身份、状态、排程与<strong>预算归属</strong>逐字不变，
        /// 且其来源窗口（可能已关闭）的账本条目与计划投影一致"。</item>
        /// </list>
        ///
        /// <strong>比较面边界</strong>：全部事实只来自 <see cref="LogicSnapshot"/> 的
        /// <strong>整数</strong>字段（窗口/预算/周期号/预留额），比较器拿不到 Transform、动画进度或
        /// Unity 帧序号，因此这里比较的时间与预算只可能是整数 Tick。
        ///
        /// 差异一律按精确字段路径登记为 <see cref="ShadowDifferenceKind.NewRuleVerifiedFact"/>
        /// （差异即回归），并先经过逐用例 + RulesVersion 的批准差异匹配；本任务<strong>不批准</strong>
        /// 任何差异。事实分组顺序固定（窗口管理器 → 逐窗口 → 资源聚合 → 并发授权 → 肾上腺素 →
        /// 计划预算归属），因此"首个差异"是稳定的。
        /// </summary>
        private static void Task07TurnWindowFacts(
            ShadowComparisonReport report, ShadowCasePolicy policy,
            LogicSnapshot legacy, LogicSnapshot shadow, string checkpoint, long eventSequence)
        {
            if (legacy == null || shadow == null) return;
            long tick = legacy.Tick;

            TurnWindowManagerSnapshot legacyManager = legacy.WindowManager;
            TurnWindowManagerSnapshot shadowManager = shadow.WindowManager;
            BattleResourceSnapshot legacyResources = legacy.Resources;
            BattleResourceSnapshot shadowResources = shadow.Resources;

            // —— ① 窗口管理器标量事实 ——
            AddNewRuleFact(report, policy, tick, checkpoint, "currentWindowId",
                legacyManager.CurrentWindowId, shadowManager.CurrentWindowId,
                "当前窗口 ID 必须一致：窗口切换只翻转提交权限，不重排或跳过窗口（任务 07）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "nextWindowTick",
                legacyManager.NextWindowTick, shadowManager.NextWindowTick,
                "下一个窗口的最早打开 Tick 必须一致（新窗口最早下一 Tick 打开，任务 07）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "nextWindowOrdinal",
                legacyManager.NextWindowOrdinal, shadowManager.NextWindowOrdinal,
                "已创建窗口数必须一致：窗口 ID 只由唯一分配器取号，创建失败不消耗（任务 07）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "lastClosedWindowId",
                legacyManager.LastClosedWindowId, shadowManager.LastClosedWindowId,
                "最近正式关闭的窗口必须一致（关闭只撤销提交权限，不结算任何计划）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "windows.count",
                legacyManager.Windows.Count, shadowManager.Windows.Count,
                "窗口集合必须一致（含已关闭窗口仍保留的可审计账本，任务 07）", eventSequence);

            // —— ② 逐窗口：按 WindowId 升序（稳定键），两侧取并集，缺项即差异 ——
            var legacyWindows = WindowsById(legacy);
            var shadowWindows = WindowsById(shadow);
            foreach (long windowId in UnionOfKeys(legacyWindows, shadowWindows))
            {
                string prefix = "windows[" + windowId.ToString(CultureInfo.InvariantCulture) + "]";
                bool hasLegacy = legacyWindows.TryGetValue(windowId, out TurnWindowSnapshot legacyWindow);
                bool hasShadow = shadowWindows.TryGetValue(windowId, out TurnWindowSnapshot shadowWindow);

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".present",
                    hasLegacy, hasShadow, "窗口账本集合必须一致（任务 07）", eventSequence);
                if (!hasLegacy || !hasShadow) continue;

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".ownerUnitId",
                    legacyWindow.OwnerUnitId, shadowWindow.OwnerUnitId,
                    "窗口拥有者必须一致：拥有者天然持有提交权，非拥有者必须另行激活并发授权", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".openedAtTick",
                    legacyWindow.OpenedAtTick, shadowWindow.OpenedAtTick,
                    "窗口打开 Tick 必须一致（只能在该 Tick 的打开阶段、确认战斗未结束后打开）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".isOpen",
                    legacyWindow.IsOpen, shadowWindow.IsOpen,
                    "正式关闭位必须一致（Tick 末由管理器翻转，窗口不结算任何计划）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".isAcceptingSubmissions",
                    legacyWindow.IsAcceptingSubmissions, shadowWindow.IsAcceptingSubmissions,
                    "接受提交位必须一致：请求关闭后立即为 false（本 Tick 后续命令稳定拒绝）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".closeReason",
                    legacyWindow.CloseReason, shadowWindow.CloseReason,
                    "关闭原因必须一致（OwnerRequested/BudgetExhausted/OwnerDied/BattleEnded）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".totalBudgetTicks",
                    legacyWindow.TotalBudgetTicks, shadowWindow.TotalBudgetTicks,
                    "窗口总预算（整数 Tick）必须一致", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".reservedBudgetTicks",
                    legacyWindow.ReservedBudgetTicks, shadowWindow.ReservedBudgetTicks,
                    "Editable 预留合计（整数 Tick）必须一致：只有 Startable 原子提交才把它转成 Spent", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".spentBudgetTicks",
                    legacyWindow.SpentBudgetTicks, shadowWindow.SpentBudgetTicks,
                    "已消费合计（整数 Tick）必须一致：锁定后任何终态都不退款", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".availableBudgetTicks",
                    legacyWindow.AvailableBudgetTicks, shadowWindow.AvailableBudgetTicks,
                    "可用预算（整数 Tick）必须一致（该计划可继续预留的上限）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".reservations.count",
                    WindowReservationCount(legacyWindow), WindowReservationCount(shadowWindow),
                    "按计划归属的预留明细条数必须一致（窗口只保存这份可审计账本）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".reservations",
                    DescribeWindowReservations(legacyWindow), DescribeWindowReservations(shadowWindow),
                    "预留明细必须一致（按 ActionPlanId 升序；与容器枚举顺序无关）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".budgetIdentity",
                    WindowBudgetIdentityHolds(legacyWindow), WindowBudgetIdentityHolds(shadowWindow),
                    "窗口预算恒等式必须成立：Reserved + Spent + Available == Total 且三项非负", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".reservationSumMatchesReserved",
                    WindowReservationSum(legacyWindow) == legacyWindow.ReservedBudgetTicks,
                    WindowReservationSum(shadowWindow) == shadowWindow.ReservedBudgetTicks,
                    "预留明细合计必须等于 Reserved（账本与逐计划明细不允许漂移）", eventSequence);
            }

            // —— ③ 战斗资源聚合（含已关闭窗口的审计账本）——
            AddNewRuleFact(report, policy, tick, checkpoint, "resources.metaResource",
                legacyResources.MetaResource, shadowResources.MetaResource,
                "局外资源必须一致：并发激活的费用只来自权威定义且恰好消费一次", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "resources.turnBudgetAvailable",
                legacyResources.TurnBudgetAvailable, shadowResources.TurnBudgetAvailable,
                "全窗口可用预算聚合必须一致", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "resources.turnBudgetReserved",
                legacyResources.TurnBudgetReserved, shadowResources.TurnBudgetReserved,
                "全窗口预留聚合必须一致", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "resources.turnBudgetSpent",
                legacyResources.TurnBudgetSpent, shadowResources.TurnBudgetSpent,
                "全窗口已消费聚合必须一致", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "resources.turnBudgetIdentity",
                ResourceBudgetIdentityHolds(legacyManager.Windows, legacyResources),
                ResourceBudgetIdentityHolds(shadowManager.Windows, shadowResources),
                "战斗资源恒等式必须成立：Available + Reserved + Spent == Σ Total（直到战斗结束清理）",
                eventSequence);

            // —— ④ 并发提交授权 ——
            ConcurrentActionSnapshot legacyAuthority = legacy.ConcurrentAction;
            ConcurrentActionSnapshot shadowAuthority = shadow.ConcurrentAction;
            AddNewRuleFact(report, policy, tick, checkpoint, "concurrentAction.hasActiveAuthorization",
                legacyAuthority.HasActiveAuthorization, shadowAuthority.HasActiveAuthorization,
                "并发授权存在性必须一致：窗口关闭即撤销，但已接受计划继续存在", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "concurrentAction.windowId",
                legacyAuthority.WindowId, shadowAuthority.WindowId,
                "授权所属窗口必须一致（撤销只对当前窗口生效）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint, "concurrentAction.playerUnitId",
                legacyAuthority.PlayerUnitId, shadowAuthority.PlayerUnitId,
                "授权受控单位必须一致（只有装配显式注入的主角单位可被激活）", eventSequence);
            AddNewRuleFact(report, policy, tick, checkpoint,
                "concurrentAction.authorizationTargetsOpenWindow",
                AuthorizationTargetsOpenWindow(legacyAuthority, legacyManager.Windows),
                AuthorizationTargetsOpenWindow(shadowAuthority, shadowManager.Windows),
                "授权必须指向仍开放且仍在接受提交的当前窗口，且受控单位不得是该窗口拥有者", eventSequence);

            // —— ⑤ 肾上腺素账本（逐单位，按 UnitId 升序）——
            var legacyLedgers = AdrenalineByUnit(legacy);
            var shadowLedgers = AdrenalineByUnit(shadow);
            AddNewRuleFact(report, policy, tick, checkpoint, "adrenaline.count",
                legacyLedgers.Count, shadowLedgers.Count,
                "肾上腺素账本集合必须一致（每单位一份，按 UnitId 升序）", eventSequence);
            foreach (long unitId in UnionOfKeys(legacyLedgers, shadowLedgers))
            {
                string prefix = "adrenaline[" + unitId.ToString(CultureInfo.InvariantCulture) + "]";
                bool hasLegacy = legacyLedgers.TryGetValue(unitId, out AdrenalineLedgerSnapshot legacyLedger);
                bool hasShadow = shadowLedgers.TryGetValue(unitId, out AdrenalineLedgerSnapshot shadowLedger);

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".present",
                    hasLegacy, hasShadow, "账本主体集合必须一致（任务 07）", eventSequence);
                if (!hasLegacy || !hasShadow) continue;

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".available",
                    legacyLedger.AvailableAdrenaline, shadowLedger.AvailableAdrenaline,
                    "Available 必须一致：不随 Tick 衰减，跨其他单位窗口保留，"
                    + "只在拥有者自己窗口打开时清零（Task07TurnWindowFacts）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".cycleId",
                    legacyLedger.CycleId, shadowLedger.CycleId,
                    "个人周期号必须一致：只在拥有者自己窗口打开时 +1（先递增周期再清零）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".reservedTotal",
                    legacyLedger.ReservedTotal, shadowLedger.ReservedTotal,
                    "反应预留合计必须一致（Available 的扣减与预留同一次原子完成）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".reservations",
                    DescribeAdrenalineReservations(legacyLedger), DescribeAdrenalineReservations(shadowLedger),
                    "逐计划预留明细必须一致（按 ActionPlanId 升序，含 ReservationCycleId）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".reservationSumMatchesReservedTotal",
                    AdrenalineReservationSum(legacyLedger) == legacyLedger.ReservedTotal,
                    AdrenalineReservationSum(shadowLedger) == shadowLedger.ReservedTotal,
                    "预留明细合计必须等于 ReservedTotal（账本与明细不允许漂移）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + ".mirrorMatchesLedger",
                    UnitMirrorMatchesLedger(legacy, legacyLedger, unitId),
                    UnitMirrorMatchesLedger(shadow, shadowLedger, unitId),
                    "单位只读镜像（Available/AdrenalineCycleId）必须与账本一致："
                    + "镜像只是投影，任何系统都不得反过来经它修改账本", eventSequence);
            }

            // —— ⑥ 跨窗口计划不变性：计划的预算投影与其来源窗口账本的联系 ——
            var legacyPlans = PlansById(legacy);
            var shadowPlans = PlansById(shadow);
            foreach (long planId in UnionOfKeys(legacyPlans, shadowPlans))
            {
                string prefix = "plans[" + planId.ToString(CultureInfo.InvariantCulture) + "].";
                bool hasLegacy = legacyPlans.TryGetValue(planId, out ActionPlanSnapshot legacyPlan);
                bool hasShadow = shadowPlans.TryGetValue(planId, out ActionPlanSnapshot shadowPlan);
                if (!hasLegacy || !hasShadow) continue;

                AddNewRuleFact(report, policy, tick, checkpoint, prefix + "budgetCostTicks",
                    legacyPlan.BudgetCostTicks, shadowPlan.BudgetCostTicks,
                    "计划的整数预算成本必须一致（Move 用路径权重 × 基础步长 + 后摇，不用 EdgeCount）",
                    eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + "reservedTurnBudgetTicks",
                    legacyPlan.ReservedTurnBudgetTicks, shadowPlan.ReservedTurnBudgetTicks,
                    "计划当前持有的预留额必须一致：锁定后为 0，Editable 释放后也为 0（任务 07）", eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + "submittedWindowLedger",
                    DescribeSubmittedWindowLedger(legacyPlan, legacyManager.Windows),
                    DescribeSubmittedWindowLedger(shadowPlan, shadowManager.Windows),
                    "计划跨窗口切换时其来源窗口账本条目必须逐字不变（已关闭窗口只更新历史账本，不重开、不转移）",
                    eventSequence);
                AddNewRuleFact(report, policy, tick, checkpoint, prefix + "budgetLedgerLinked",
                    PlanBudgetLedgerLinked(legacyPlan, legacyManager.Windows),
                    PlanBudgetLedgerLinked(shadowPlan, shadowManager.Windows),
                    "计划预算投影必须与其来源窗口账本条目一致（账本漂移即差异）", eventSequence);
            }
        }

        /// <summary>逐窗口快照（按 <c>WindowId</c> 升序使用；同 ID 重复是快照损坏，后写覆盖）。</summary>
        private static Dictionary<long, TurnWindowSnapshot> WindowsById(LogicSnapshot snapshot)
        {
            var map = new Dictionary<long, TurnWindowSnapshot>();
            if (snapshot == null || snapshot.WindowManager == null || snapshot.WindowManager.Windows == null)
                return map;
            IReadOnlyList<TurnWindowSnapshot> windows = snapshot.WindowManager.Windows;
            for (int i = 0; i < windows.Count; i++)
            {
                TurnWindowSnapshot window = windows[i];
                if (window != null) map[window.WindowId] = window;
            }
            return map;
        }

        /// <summary>逐单位肾上腺素账本（按 <c>UnitId</c> 升序使用）。</summary>
        private static Dictionary<long, AdrenalineLedgerSnapshot> AdrenalineByUnit(LogicSnapshot snapshot)
        {
            var map = new Dictionary<long, AdrenalineLedgerSnapshot>();
            if (snapshot == null || snapshot.Resources == null || snapshot.Resources.AdrenalineLedgers == null)
                return map;
            IReadOnlyList<AdrenalineLedgerSnapshot> ledgers = snapshot.Resources.AdrenalineLedgers;
            for (int i = 0; i < ledgers.Count; i++)
            {
                AdrenalineLedgerSnapshot ledger = ledgers[i];
                if (ledger != null) map[ledger.UnitId] = ledger;
            }
            return map;
        }

        private static int WindowReservationCount(TurnWindowSnapshot window)
            => window == null || window.Reservations == null ? 0 : window.Reservations.Count;

        /// <summary>窗口预留明细的规范文本（按 <c>ActionPlanId</c> 升序 ⇒ 与容器顺序无关）。</summary>
        private static string DescribeWindowReservations(TurnWindowSnapshot window)
        {
            if (window == null || window.Reservations == null || window.Reservations.Count == 0) return "<none>";
            var entries = new List<TurnWindowReservationSnapshot>(window.Reservations);
            entries.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) builder.Append(';');
                builder.Append(entries[i].ActionPlanId.ToString(CultureInfo.InvariantCulture))
                    .Append(':')
                    .Append(entries[i].ReservedTicks.ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static int WindowReservationSum(TurnWindowSnapshot window)
        {
            if (window == null || window.Reservations == null) return 0;
            int sum = 0;
            for (int i = 0; i < window.Reservations.Count; i++)
            {
                if (window.Reservations[i] != null) sum += window.Reservations[i].ReservedTicks;
            }
            return sum;
        }

        /// <summary>窗口预算恒等式：<c>Reserved + Spent + Available == Total</c> 且三项非负。</summary>
        private static bool WindowBudgetIdentityHolds(TurnWindowSnapshot window)
        {
            if (window == null) return false;
            if (window.ReservedBudgetTicks < 0 || window.SpentBudgetTicks < 0 || window.AvailableBudgetTicks < 0)
                return false;
            long sum = (long)window.ReservedBudgetTicks + window.SpentBudgetTicks + window.AvailableBudgetTicks;
            return sum == window.TotalBudgetTicks;
        }

        /// <summary>全部窗口（含已关闭窗口的审计账本）的总预算合计。</summary>
        private static long SumWindowTotals(IReadOnlyList<TurnWindowSnapshot> windows)
        {
            if (windows == null) return 0L;
            long total = 0L;
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i] != null) total += windows[i].TotalBudgetTicks;
            }
            return total;
        }

        /// <summary>战斗资源恒等式：<c>Available + Reserved + Spent == Σ Total</c>。</summary>
        private static bool ResourceBudgetIdentityHolds(
            IReadOnlyList<TurnWindowSnapshot> windows, BattleResourceSnapshot resources)
        {
            if (resources == null) return false;
            if (resources.TurnBudgetAvailable < 0L || resources.TurnBudgetReserved < 0L
                || resources.TurnBudgetSpent < 0L) return false;
            long sum = resources.TurnBudgetAvailable + resources.TurnBudgetReserved + resources.TurnBudgetSpent;
            return sum == SumWindowTotals(windows);
        }

        /// <summary>
        /// 授权不变量：未激活时为真；激活时必须指向<strong>仍开放且仍在接受提交</strong>的当前窗口，
        /// 且受控单位不得是该窗口的拥有者（拥有者天然持有提交权）。
        /// </summary>
        private static bool AuthorizationTargetsOpenWindow(
            ConcurrentActionSnapshot authority, IReadOnlyList<TurnWindowSnapshot> windows)
        {
            if (authority == null || !authority.HasActiveAuthorization) return true;
            if (windows == null) return false;
            for (int i = 0; i < windows.Count; i++)
            {
                TurnWindowSnapshot window = windows[i];
                if (window == null || window.WindowId != authority.WindowId) continue;
                return window.IsOpen && window.IsAcceptingSubmissions && window.OwnerUnitId != authority.PlayerUnitId;
            }
            return false;
        }

        private static AdrenalineLedgerSnapshot FindLedger(LogicSnapshot snapshot, long unitId)
        {
            if (snapshot == null || snapshot.Resources == null || snapshot.Resources.AdrenalineLedgers == null)
                return null;
            IReadOnlyList<AdrenalineLedgerSnapshot> ledgers = snapshot.Resources.AdrenalineLedgers;
            for (int i = 0; i < ledgers.Count; i++)
            {
                if (ledgers[i] != null && ledgers[i].UnitId == unitId) return ledgers[i];
            }
            return null;
        }

        private static int AdrenalineReservationSum(AdrenalineLedgerSnapshot ledger)
        {
            if (ledger == null || ledger.Reservations == null) return 0;
            int sum = 0;
            for (int i = 0; i < ledger.Reservations.Count; i++)
            {
                if (ledger.Reservations[i] != null) sum += ledger.Reservations[i].ReservedAmount;
            }
            return sum;
        }

        /// <summary>逐计划肾上腺素预留的规范文本（按 <c>ActionPlanId</c> 升序，含周期号）。</summary>
        private static string DescribeAdrenalineReservations(AdrenalineLedgerSnapshot ledger)
        {
            if (ledger == null || ledger.Reservations == null || ledger.Reservations.Count == 0) return "<none>";
            var entries = new List<AdrenalineReservationSnapshot>(ledger.Reservations);
            entries.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) builder.Append(';');
                builder.Append(entries[i].ActionPlanId.ToString(CultureInfo.InvariantCulture))
                    .Append(':')
                    .Append(entries[i].ReservedAmount.ToString(CultureInfo.InvariantCulture))
                    .Append('@')
                    .Append(entries[i].ReservationCycleId.ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        /// <summary>
        /// 单位只读镜像（<c>units[i].AvailableAdrenaline</c>/<c>AdrenalineCycleId</c>）
        /// 是否与账本一致；单位不在快照里时视为不一致（集合漂移本身就是差异）。
        /// </summary>
        private static bool UnitMirrorMatchesLedger(LogicSnapshot snapshot, AdrenalineLedgerSnapshot ledger, long unitId)
        {
            if (snapshot == null || snapshot.Units == null || ledger == null) return false;
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                UnitSnapshot unit = snapshot.Units[i];
                if (unit == null || unit.UnitId != unitId) continue;
                return unit.AvailableAdrenaline == ledger.AvailableAdrenaline
                       && unit.AdrenalineCycleId == ledger.CycleId;
            }
            return false;
        }

        /// <summary>
        /// 计划来源窗口账本的只读投影：<c>w=&lt;id&gt;;open=..;accepting=..;reserved=..;spent=..</c>
        /// （窗口已从集合中消失时为显式标记，绝不猜）。
        /// </summary>
        private static string DescribeSubmittedWindowLedger(
            ActionPlanSnapshot plan, IReadOnlyList<TurnWindowSnapshot> windows)
        {
            if (plan == null) return "<plan-absent>";
            if (plan.SubmittedWindowId == 0L) return "<none>";
            TurnWindowSnapshot window = FindWindowSnapshot(windows, plan.SubmittedWindowId);
            if (window == null) return "w=" + plan.SubmittedWindowId.ToString(CultureInfo.InvariantCulture) + ";missing";
            return "w=" + window.WindowId.ToString(CultureInfo.InvariantCulture)
                   + ";open=" + (window.IsOpen ? "true" : "false")
                   + ";accepting=" + (window.IsAcceptingSubmissions ? "true" : "false")
                   + ";reserved=" + window.ReservedBudgetTicks.ToString(CultureInfo.InvariantCulture)
                   + ";spent=" + window.SpentBudgetTicks.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 计划预算投影与其来源窗口账本条目是否一致：
        /// 账本里该计划持有的预留额必须等于计划的 <c>ReservedTurnBudgetTicks</c>
        /// （锁定后两者都为 0；Editable 释放后同理）。没有来源窗口时要求计划的预留投影为 0。
        /// </summary>
        private static bool PlanBudgetLedgerLinked(
            ActionPlanSnapshot plan, IReadOnlyList<TurnWindowSnapshot> windows)
        {
            if (plan == null) return false;
            if (plan.SubmittedWindowId == 0L) return plan.ReservedTurnBudgetTicks == 0;
            TurnWindowSnapshot window = FindWindowSnapshot(windows, plan.SubmittedWindowId);
            if (window == null) return false;
            return WindowReservedFor(window, plan.ActionPlanId) == plan.ReservedTurnBudgetTicks;
        }

        private static TurnWindowSnapshot FindWindowSnapshot(IReadOnlyList<TurnWindowSnapshot> windows, long windowId)
        {
            if (windows == null) return null;
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i] != null && windows[i].WindowId == windowId) return windows[i];
            }
            return null;
        }

        private static int WindowReservedFor(TurnWindowSnapshot window, long planId)
        {
            if (window == null || window.Reservations == null) return 0;
            for (int i = 0; i < window.Reservations.Count; i++)
            {
                TurnWindowReservationSnapshot reservation = window.Reservations[i];
                if (reservation != null && reservation.ActionPlanId == planId) return reservation.ReservedTicks;
            }
            return 0;
        }

        // -------- Legacy 只读观测 vs 新模拟快照（真实可比较字段） --------

        /// <summary>
        /// 用 Legacy 侧只读观测（<see cref="LegacyLogicObservation"/>）与新模拟快照
        /// 做字段级比较。
        ///
        /// 对齐规则与 Logic 世界对 Logic 世界完全一致：**按逻辑 Tick 配对**。
        /// Legacy 侧报告自己的 <see cref="LegacyLogicObservation.Tick"/>（旧时间线真实 Tick），
        /// Shadow 侧报告 <see cref="LogicSnapshot.Tick"/>；两者不一致即计入未对齐并显式报告，
        /// 绝不按帧序号强行配对。
        ///
        /// 比较集合是<strong>显式白名单</strong>（<see cref="LegacyLogicObservation.ComparableFieldPaths"/>）：
        /// 每条都必须是"两侧都能从各自世界的真实事实独立推出"的字段。未实现字段不在其中，
        /// 由 <see cref="TemporarilyUncomparableFacts"/> 逐条登记（对象路径/原因/负责任务/清零门槛）。
        /// </summary>
        private static void CompareObservedLegacyCheckpoints(
            ShadowComparisonReport report, ShadowCasePolicy policy, ShadowComparisonInput input)
        {
            var observations = input.LegacyObservations;
            var shadow = input.ShadowCheckpoints;

            if (observations.Count > input.Configuration.MaxStepsPerComparison)
            {
                report.MarkBudgetOverrun(ShadowComparisonCodes.ShadowBudgetOverrun
                    + "|checkpoints=" + observations.Count
                    + "|budget=" + input.Configuration.MaxStepsPerComparison);
                return;
            }

            int shadowIndex = 0;
            for (int i = 0; i < observations.Count; i++)
            {
                var observation = observations[i];
                if (observation == null)
                {
                    report.UnalignedCheckpoints++;
                    continue;
                }

                while (shadowIndex < shadow.Count
                       && shadow[shadowIndex] != null
                       && shadow[shadowIndex].Tick < observation.Tick)
                {
                    shadowIndex++;
                }

                if (shadowIndex >= shadow.Count || shadow[shadowIndex] == null)
                {
                    report.UnalignedCheckpoints++;
                    continue;
                }

                var shadowSnapshot = shadow[shadowIndex];
                if (shadowSnapshot.Tick != observation.Tick)
                {
                    long legacyTick = observation.Tick;
                    long shadowTick = shadowSnapshot.Tick;
                    report.UnalignedCheckpoints++;
                    report.AddDifference(new ShadowFieldDifference(
                        ShadowDifferenceKind.InfrastructureFact, legacyTick,
                        observation.Checkpoint + "@" + legacyTick, "checkpoint.tick",
                        legacyTick.ToString(CultureInfo.InvariantCulture),
                        shadowTick.ToString(CultureInfo.InvariantCulture),
                        "同一检查点位置上的逻辑 Tick 不同：Legacy 观测无法与 Shadow 快照按逻辑 Tick 对齐",
                        -1));
                    shadowIndex++;
                    continue;
                }

                shadowIndex++;
                report.ComparedCheckpoints++;
                CompareObservedCheckpoint(report, policy, observation, shadowSnapshot,
                    input.SlotOrder, input.EventSequenceAt(observation.Tick));
            }

            if (shadowIndex < shadow.Count) report.UnalignedCheckpoints += shadow.Count - shadowIndex;

            // 新模拟未追上的 Tick（单帧追赶上限）同样阻断等价声明：本次比较没有覆盖全部检查点。
            if (input.ShadowTickDeficit > 0)
            {
                report.UnalignedCheckpoints += input.ShadowTickDeficit;
                report.AddDifference(new ShadowFieldDifference(
                    ShadowDifferenceKind.InfrastructureFact,
                    observations.Count > 0 ? observations[observations.Count - 1].Tick : -1L,
                    "LateUpdate", "shadow.tickDeficit",
                    "0", input.ShadowTickDeficit.ToString(CultureInfo.InvariantCulture),
                    "新模拟未能追上旧时间线的逻辑 Tick（单帧追赶上限）：本次比较未覆盖全部检查点",
                    -1));
            }
        }

        /// <summary>单个已对齐检查点的字段级比较（Legacy 观测 vs 新模拟快照）。</summary>
        private static void CompareObservedCheckpoint(
            ShadowComparisonReport report, ShadowCasePolicy policy,
            LegacyLogicObservation observation, LogicSnapshot shadow,
            IReadOnlyList<LegacySlotOrderEntry> slotOrder, long relatedEventSequence)
        {
            string checkpoint = observation.Checkpoint + "@"
                + observation.Tick.ToString(CultureInfo.InvariantCulture);

            // 字段集合的唯一权威是 LegacyLogicObservation.ComparableFieldPaths：
            // 逐个字段路径分派（未知路径**显式报错**，绝不静默跳过）。
            var paths = LegacyLogicObservation.ComparableFieldPaths;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.Equals(path, "tick", StringComparison.Ordinal))
                {
                    // 对齐键本身：两侧各自报告的逻辑 Tick 必须相等。
                    AddInfrastructure(report, observation.Tick, checkpoint, path,
                        observation.Tick, shadow.Tick,
                        "逻辑 Tick 是对齐键，两侧必须报告同一个 Tick", relatedEventSequence);
                    continue;
                }

                if (string.Equals(path, "units.count", StringComparison.Ordinal))
                {
                    // 单位数量：旧场景真实单位数 vs 新世界初始化出的单位数。
                    AddInfrastructure(report, observation.Tick, checkpoint, path,
                        observation.ObservedUnitCount, shadow.Units.Count,
                        "单位数量必须相等（旧场景权威计数 vs 新世界初始化结果）", relatedEventSequence);
                    continue;
                }

                // —— 任务 04：battleEnd 与旧排程计数（真读旧运行事实）——

                if (string.Equals(path, "battleEnd.isEnded", StringComparison.Ordinal))
                {
                    AddInfrastructure(report, observation.Tick, checkpoint, path,
                        observation.BattleEndedLive, shadow.BattleEnd.IsEnded,
                        "旧活动 BattleManager 的结束标志 vs 新 BattleEndSnapshot.IsEnded"
                        + "（两侧都是各自世界的真实活动事实，必须相等）", relatedEventSequence);
                    continue;
                }

                if (string.Equals(path, "battleEnd.resultCode", StringComparison.Ordinal))
                {
                    // 旧侧没有结果码字段，只有结束显示文本；观测实现把它按冻结规则
                    // 解析成与 VictoryDefinition 同域的码（见 LegacyLogicObservation 注释）。
                    //
                    // "尚无结果"的表示必须归一：旧侧观测契约把 null 折叠成空串
                    // （LegacyLogicObservation 构造函数），新侧未结束时 ResultCode 为 null。
                    // 二者是同一个事实（都表示"没有结果码"），因此按缺席归一后比较；
                    // 只要任一侧真的给出了结果码，比较仍然逐字判定——这不是容忍范围扩大。
                    AddInfrastructure(report, observation.Tick, checkpoint, path,
                        NormalizeAbsentCode(observation.BattleResultCodeLive),
                        NormalizeAbsentCode(shadow.BattleEnd.ResultCode),
                        "旧侧结束事实派生出的结果码 vs 新 BattleEndSnapshot.ResultCode"
                        + "（空串与 null 同义：都表示尚无结果码）", relatedEventSequence);
                    continue;
                }

                if (string.Equals(path, "scheduledEventCount", StringComparison.Ordinal))
                {
                    // 旧排程表条目数（真读 BattleTimeline.ScheduledEventCount）vs 新内核当前排程条目数。
                    // 新内核在任务 04 阶段不产生排程条目（计划属任务 05），因此两侧都为 0；
                    // 这条比较证明"旧侧排程事实确实被读过"，而不是结构等价的声明。
                    AddInfrastructure(report, observation.Tick, checkpoint, path,
                        observation.ScheduledEventCountLive, CountShadowScheduledEntries(shadow),
                        "旧活动 BattleTimeline 的未执行排程条目数 vs 新内核当前排程条目数"
                        + "（这条断言的鉴别力是「旧侧事实被真读」，不是「两侧结构等价」）",
                        relatedEventSequence);
                    continue;
                }

                if (path.StartsWith("units[i].", StringComparison.Ordinal))
                {
                    CompareObservedUnitFacts(report, observation, shadow, path, checkpoint,
                        slotOrder, relatedEventSequence);
                    continue;
                }

                throw new LogicDefinitionException(
                    "SHADOW_COMPARABLE_FIELD_NOT_IMPLEMENTED", path);
            }

            // 未实现字段：逐条登记（不是静默丢弃，也不是默认值冒充"已比较"）。
            TemporarilyUncomparableFacts(report, policy, null, shadow, checkpoint, relatedEventSequence);
        }

        /// <summary>
        /// 逐单位比较一个身份字段族（<c>units[i].slotId|unitId|definitionId|factionId</c>）。
        ///
        /// 顺序契约：旧侧观测按唯一权威顺序（<c>SlotId</c> Ordinal 升序 ⇒ <c>UnitId</c> 升序）排列，
        /// 新侧 <c>Units</c> 也由同一顺序初始化，因此两侧下标一一对应；
        /// **槽位映射本身仍被独立核对**（<see cref="ResolveSlotId"/> 只读定义侧槽位表）。
        /// </summary>
        private static void CompareObservedUnitFacts(
            ShadowComparisonReport report, LegacyLogicObservation observation, LogicSnapshot shadow,
            string fieldPath, string checkpoint, IReadOnlyList<LegacySlotOrderEntry> slotOrder,
            long relatedEventSequence)
        {
            var observedUnits = observation.Units;
            for (int i = 0; i < observedUnits.Count; i++)
            {
                var unit = observedUnits[i];
                if (unit == null) continue;

                string prefix = "units[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                UnitSnapshot shadowUnit = FindShadowUnit(shadow, unit.UnitId);

                if (string.Equals(fieldPath, "units[i].slotId", StringComparison.Ordinal))
                {
                    AddInfrastructure(report, observation.Tick, checkpoint, prefix + "slotId",
                        unit.SlotId, ResolveSlotId(slotOrder, shadowUnit),
                        "槽位映射必须一致：旧侧显式槽位绑定 vs 定义侧槽位→UnitId 映射", relatedEventSequence);
                    continue;
                }

                if (shadowUnit == null)
                {
                    // 数据完整性事实：旧侧观测到的槽位在新世界里必须存在对应单位。
                    AddInfrastructure(report, observation.Tick, checkpoint, prefix + "unitId",
                        unit.UnitId, -1L,
                        "旧侧观测到的槽位必须在新世界里有对应单位（UnitId 由槽位顺序唯一决定）",
                        relatedEventSequence);
                    continue;
                }

                switch (fieldPath)
                {
                    case "units[i].unitId":
                        AddInfrastructure(report, observation.Tick, checkpoint, prefix + "unitId",
                            unit.UnitId, shadowUnit.UnitId,
                            "UnitId 由 SlotId 的 Ordinal 顺序唯一分配，两侧必须一致", relatedEventSequence);
                        break;
                    case "units[i].definitionId":
                        AddInfrastructure(report, observation.Tick, checkpoint, prefix + "definitionId",
                            unit.DefinitionId, shadowUnit.DefinitionId,
                            "单位定义必须来自同一 Encounter 槽位", relatedEventSequence);
                        break;
                    case "units[i].factionId":
                        AddInfrastructure(report, observation.Tick, checkpoint, prefix + "factionId",
                            unit.FactionId, shadowUnit.FactionId,
                            "阵营来自槽位、创建后不可变，两侧必须一致", relatedEventSequence);
                        break;
                    case "units[i].state":
                        AddInfrastructure(report, observation.Tick, checkpoint, prefix + "state",
                            unit.LegacyStateFlagsLive, shadowUnit.State,
                            "旧活动 CombatUnit 的状态族标签（由旧 bool 按冻结规则推导）"
                            + " vs 新 UnitStateMachine 的权威状态", relatedEventSequence);
                        break;
                    case "units[i].healthQ10":
                        AddInfrastructure(report, observation.Tick, checkpoint, prefix + "healthQ10",
                            QuantizeLegacyHealth(unit), shadowUnit.HealthQ10,
                            "旧活动 CombatUnit.CurrentHealth 经同一量化约定得到的 Q10 值"
                            + " vs 新内核 HealthQ10", relatedEventSequence);
                        break;
                    case "units[i].position":
                        AddInfrastructure(report, observation.Tick, checkpoint, prefix + "position",
                            unit.GridPositionLiveX + "," + unit.GridPositionLiveY,
                            shadowUnit.X + "," + shadowUnit.Y,
                            "旧活动 CombatUnit.GridPosition vs 新内核单位位置", relatedEventSequence);
                        break;
                    case "units[i].facing":
                        AddInfrastructure(report, observation.Tick, checkpoint, prefix + "facing",
                            unit.FacingLive, shadowUnit.Facing,
                            "旧活动 CombatUnit.FacingDirection vs 新内核朝向", relatedEventSequence);
                        break;
                }
            }
        }

        /// <summary>
        /// 旧侧生命（浮点）→ Q10 整数：使用与新内核<strong>同一</strong>量化约定
        /// （<c>BattleSimulation.QuantizeHealth</c>，RoundHalfUp × 1024），
        /// 不在这里另写一份公式。不可采样时返回显式标记（不是 0）。
        /// </summary>
        private static string QuantizeLegacyHealth(LegacyLogicUnitObservation unit)
        {
            if (unit == null || !unit.HasCurrentHealthLive) return LegacyNotSampled;
            return ProjectHero.Logic.Simulation.BattleSimulation
                .QuantizeHealth(unit.CurrentHealthLive)
                .ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把"没有结果码"的两种表示（<c>null</c> 与空串）归一成同一种<strong>缺席事实</strong>。
        ///
        /// 旧侧观测契约不允许 <c>null</c>（构造函数折叠成空串），新侧未结束时
        /// <c>BattleEndSnapshot.ResultCode</c> 为 <c>null</c>；两者表示同一个事实。
        /// 归一<strong>只覆盖"缺席"</strong>：任一侧给出非空结果码时仍然逐字比较。
        /// </summary>
        private static string NormalizeAbsentCode(string resultCode)
            => string.IsNullOrEmpty(resultCode) ? null : resultCode;

        /// <summary>
        /// 新内核当前的"排程条目数"：计划 + 意图 + 活动持续效果。
        /// 任务 04 阶段计划与意图集合恒为空、效果集合由持续效果内核维护，
        /// 因此这个计数是"新侧真实活动事实"而不是占位常量。
        /// </summary>
        private static int CountShadowScheduledEntries(LogicSnapshot shadow)
            => shadow.Plans.Count + shadow.Intents.Count + shadow.Effects.Count;

        private static UnitSnapshot FindShadowUnit(LogicSnapshot shadow, long unitId)
        {
            if (shadow == null) return null;
            var units = shadow.Units;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] != null && units[i].UnitId == unitId) return units[i];
            }
            return null;
        }

        /// <summary>
        /// 新侧某个单位对应的槽位 ID：由定义侧槽位顺序（<c>SlotId</c> Ordinal 升序 ⇒ <c>UnitId</c>）
        /// 解析，<strong>不读取 Legacy 观测</strong>——因此"旧侧把单位绑定到错误槽位"仍会被发现。
        /// </summary>
        private static string ResolveSlotId(
            IReadOnlyList<LegacySlotOrderEntry> slotOrder, UnitSnapshot shadowUnit)
        {
            if (shadowUnit == null) return LegacyNotSampled;
            if (slotOrder == null) return LegacyNotSampled;
            for (int i = 0; i < slotOrder.Count; i++)
            {
                var entry = slotOrder[i];
                if (entry == null) continue;
                if (entry.UnitId != shadowUnit.UnitId) continue;
                return entry.SlotId;
            }
            return LegacyNotSampled;
        }

        // -------- 必须相等的基础设施事实 --------

        private static void InfrastructureFacts(
            ShadowComparisonReport report, ShadowCasePolicy policy,
            LogicSnapshot legacy, LogicSnapshot shadow, string checkpoint, long eventSequence)
        {
            AddInfrastructure(report,
                legacy.Tick, checkpoint, "tick",
                legacy.Tick, shadow.Tick, "逻辑 Tick 必须相等（按 Tick 对齐）", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "rulesVersion",
                legacy.RulesVersion, shadow.RulesVersion, "同一 RulesVersion 才可比较", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "battleDefinitionHash",
                legacy.BattleDefinitionHash, shadow.BattleDefinitionHash,
                "同一定义哈希才可比较", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "encounterId",
                legacy.EncounterId, shadow.EncounterId, "同一 Encounter 才可比较", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "units.count",
                legacy.Units.Count, shadow.Units.Count, "单位数量必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "plans.count",
                legacy.Plans.Count, shadow.Plans.Count, "活动计划数必须相等（任务 05 扩展后仍成立）", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "actorLanes.count",
                legacy.ActorLanes.Count, shadow.ActorLanes.Count, "Lane 数必须相等（任务 05 扩展后仍成立）", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "movementSegments.count",
                legacy.MovementSegments.Count, shadow.MovementSegments.Count,
                "移动段数必须相等（任务 06 扩展后仍成立）", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "reservations.count",
                legacy.Reservations.Count, shadow.Reservations.Count,
                "Reservation 数必须相等（任务 06 扩展后仍成立）", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "intents.count",
                legacy.Intents.Count, shadow.Intents.Count, "Intent 数必须相等（任务 08 扩展后仍成立）", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "effects.count",
                legacy.Effects.Count, shadow.Effects.Count, "效果数必须相等（任务 04 扩展后仍成立）", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "scheduleRevision",
                legacy.ScheduleRevision, shadow.ScheduleRevision, "排程修订必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "nextCommandSequence",
                legacy.NextCommandSequence, shadow.NextCommandSequence, "命令序号计数器必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "nextEventSequence",
                legacy.NextEventSequence, shadow.NextEventSequence, "事件序号计数器必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "nextUnitId",
                legacy.NextUnitId, shadow.NextUnitId, "单位 ID 计数器必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "nextActionPlanId",
                legacy.NextActionPlanId, shadow.NextActionPlanId, "计划 ID 计数器必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "nextReactionOpportunityId",
                legacy.NextReactionOpportunityId, shadow.NextReactionOpportunityId,
                "反应机会 ID 计数器必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "nextWindowId",
                legacy.NextWindowId, shadow.NextWindowId, "窗口 ID 计数器必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "nextEffectId",
                legacy.NextEffectId, shadow.NextEffectId, "效果 ID 计数器必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "history.recordCount",
                legacy.History.RecordCount, shadow.History.RecordCount, "归档记录数必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "history.digest",
                legacy.History.Digest, shadow.History.Digest, "增量归档摘要必须相等", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "rng.algorithmVersion",
                legacy.Rng.AlgorithmVersion, shadow.Rng.AlgorithmVersion,
                "RNG 算法版本必须相等（确定性基础设施事实）", eventSequence);

            AddInfrastructure(report,
                legacy.Tick, checkpoint, "rng.state",
                legacy.Rng.State, shadow.Rng.State,
                "RNG 状态必须相等（同种子同消耗 ⇒ 同状态；这是确定性基础设施事实）", eventSequence);

            var legacyUnits = legacy.Units;
            var shadowUnits = shadow.Units;
            int count = Math.Min(legacyUnits.Count, shadowUnits.Count);
            for (int i = 0; i < count; i++)
            {
                string prefix = "units[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                AddInfrastructure(report, legacy.Tick, checkpoint,
                    prefix + ".unitId", legacyUnits[i].UnitId, shadowUnits[i].UnitId,
                    "单位身份必须逐位相等", eventSequence);
                AddInfrastructure(report, legacy.Tick, checkpoint,
                    prefix + ".definitionId", legacyUnits[i].DefinitionId, shadowUnits[i].DefinitionId,
                    "单位定义必须相等", eventSequence);
                AddInfrastructure(report, legacy.Tick, checkpoint,
                    prefix + ".factionId", legacyUnits[i].FactionId, shadowUnits[i].FactionId,
                    "阵营来自定义、创建后不可变，必须相等", eventSequence);
            }
        }

        // -------- 按新规则验证的事实 --------

        private static void NewRuleFacts(
            ShadowComparisonReport report, ShadowCasePolicy policy,
            LogicSnapshot legacy, LogicSnapshot shadow, string checkpoint, long eventSequence)
        {
            var legacyUnits = legacy.Units;
            var shadowUnits = shadow.Units;
            int count = Math.Min(legacyUnits.Count, shadowUnits.Count);
            for (int i = 0; i < count; i++)
            {
                string prefix = "units[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                AddNewRuleFact(report, policy, legacy.Tick, checkpoint,
                    prefix + ".position", legacyUnits[i].X + "," + legacyUnits[i].Y,
                    shadowUnits[i].X + "," + shadowUnits[i].Y,
                    "位置按新规则（LogicGrid + 移动段）验证；任务 06 扩展检查点", eventSequence);
                AddNewRuleFact(report, policy, legacy.Tick, checkpoint,
                    prefix + ".facing", legacyUnits[i].Facing, shadowUnits[i].Facing,
                    "朝向按新规则验证；任务 06 扩展检查点", eventSequence);
                AddNewRuleFact(report, policy, legacy.Tick, checkpoint,
                    prefix + ".healthQ10", legacyUnits[i].HealthQ10, shadowUnits[i].HealthQ10,
                    "血量按新规则（分通道伤害 Q10）验证；任务 04/08 扩展检查点", eventSequence);
                AddNewRuleFact(report, policy, legacy.Tick, checkpoint,
                    prefix + ".isAlive", legacyUnits[i].IsAlive, shadowUnits[i].IsAlive,
                    "存活状态按新规则验证；任务 04 扩展检查点", eventSequence);
            }
        }

        // -------- 限期清零的暂不可比较字段 --------

        private static void TemporarilyUncomparableFacts(
            ShadowComparisonReport report, ShadowCasePolicy policy,
            LogicSnapshot legacy, LogicSnapshot shadow, string checkpoint, long eventSequence)
        {
            var registry = policy.TemporarilyUncomparable;
            if (registry.Count == 0) return;

            // 观察绑定的逻辑 Tick：优先取 Legacy 侧快照，其次取新侧快照。
            // 走 Legacy 观测通道时 Legacy 侧没有 LogicSnapshot（这正是"不伪造快照"的含义），
            // 因此这里用已对齐的新侧 Tick——两侧 Tick 相等是进入本方法的前提。
            LogicSnapshot tickSource = legacy ?? shadow;
            long logicalTick = tickSource != null ? tickSource.Tick : -1L;

            for (int i = 0; i < registry.Count; i++)
            {
                var field = registry[i];
                string legacyValue = ResolveLegacyValue(field, legacy);
                string shadowValue = ResolveShadowValue(field, shadow);
                if (legacyValue == null && shadowValue == null) continue;

                AddTemporarilyUncomparable(report, logicalTick, checkpoint,
                    field.Id, legacyValue, shadowValue,
                    "owner=" + field.OwnerTask + " gate=" + field.RemovalGate + " reason=" + field.Reason);
            }
        }

        private static string ResolveLegacyValue(TemporarilyUncomparableField field, LogicSnapshot snapshot)
        {
            // 暂不可比较字段的 Legacy 侧全部位于旧 Unity 对象上（不在 Logic 快照内），
            // 且其时间语义与只读检查点不兼容，因此一律显式标记为"不可采样"。
            // 任务 04–09 每完成一个子系统就应把对应字段移出本类别（而不是转成永久批准差异）。
            _ = field;
            _ = snapshot;
            return LegacyNotSampled;
        }

        private static string ResolveShadowValue(TemporarilyUncomparableField field, LogicSnapshot snapshot)
        {
            var unit = FirstUnit(snapshot);
            if (unit == null) return null;
            switch (field.Field)
            {
                case "availableAdrenaline":
                    return unit.AvailableAdrenaline.ToString(CultureInfo.InvariantCulture);
                default:
                    return LegacyNotSampled;
            }
        }

        private static UnitSnapshot FirstUnit(LogicSnapshot snapshot)
            => snapshot != null && snapshot.Units.Count > 0 ? snapshot.Units[0] : null;

        // -------- 差异判定与批准匹配 --------

        /// <summary>基础设施事实：差异无条件记录，且**永不**被批准差异匹配（不变量 22）。</summary>
        private static void AddInfrastructure(
            ShadowComparisonReport report, long logicalTick, string checkpoint, string fieldPath,
            object legacyValue, object shadowValue, string reason, long relatedEventSequence)
        {
            report.ComparedFieldObservations++;
            string legacyText = Format(legacyValue);
            string shadowText = Format(shadowValue);
            if (string.Equals(legacyText, shadowText, StringComparison.Ordinal)) return;

            report.AddDifference(new ShadowFieldDifference(
                ShadowDifferenceKind.InfrastructureFact, logicalTick, checkpoint, fieldPath,
                legacyText, shadowText, reason, relatedEventSequence));
        }

        /// <summary>限期清零字段：始终登记一条观察（Legacy 侧显式为"不可采样"）。</summary>
        private static void AddTemporarilyUncomparable(
            ShadowComparisonReport report, long logicalTick, string checkpoint, string fieldPath,
            string legacyValue, string shadowValue, string reason)
        {
            report.AddDifference(new ShadowFieldDifference(
                ShadowDifferenceKind.TemporarilyUncomparable, logicalTick, checkpoint, fieldPath,
                legacyValue ?? "<absent>", shadowValue ?? "<absent>", reason, -1));
        }

        /// <summary>按新规则验证的事实：差异先查逐用例批准差异，未命中即非预期差异。</summary>
        private static void AddNewRuleFact(
            ShadowComparisonReport report, ShadowCasePolicy policy,
            long logicalTick, string checkpoint, string fieldPath,
            object legacyValue, object shadowValue, string reason, long relatedEventSequence)
        {
            string legacyText = Format(legacyValue);
            string shadowText = Format(shadowValue);
            if (string.Equals(legacyText, shadowText, StringComparison.Ordinal)) return;

            ShadowDifferenceKind kind = MatchesApproval(policy, fieldPath, checkpoint)
                ? ShadowDifferenceKind.ApprovedDifference
                : ShadowDifferenceKind.NewRuleVerifiedFact;

            report.AddDifference(new ShadowFieldDifference(
                kind, logicalTick, checkpoint, fieldPath, legacyText, shadowText, reason, relatedEventSequence));
        }

        private static bool MatchesApproval(ShadowCasePolicy policy, string fieldPath, string checkpoint)
        {
            for (int i = 0; i < policy.Approvals.Count; i++)
            {
                var approval = policy.Approvals[i];
                if (!string.Equals(approval.FieldPath, fieldPath, StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(approval.Checkpoint)
                    && !string.Equals(approval.Checkpoint, checkpoint, StringComparison.Ordinal)) continue;
                if (!string.Equals(approval.CaseId, policy.CaseId, StringComparison.Ordinal)) continue;
                if (!string.Equals(approval.RulesVersion, policy.RulesVersion, StringComparison.Ordinal)) continue;
                return true;
            }

            return false;
        }

        private static string Format(object value)
        {
            if (value == null) return "<null>";
            if (value is string text) return text;
            if (value is bool flag) return flag ? "true" : "false";
            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }
    }
}
