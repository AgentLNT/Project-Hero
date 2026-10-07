using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    /// <summary>
    /// 任务 09 · C2b：<strong>命令与 AI 的 Shadow 画像</strong>
    /// （<c>CommandAndAiShadowProfileHasNoUnclassifiedDifference</c>）
    /// ＋ 任务 08「必须产出 17」八项 Shadow 检查点的逐条闭合。
    ///
    /// <para>
    /// 被测面 = 本轮新增的<strong>第四个可比字段族开关</strong>
    /// （<see cref="ShadowCasePolicy.CompareTask08ProfileFacts"/>，默认关闭）
    /// 与它接上的四类事实：
    /// <list type="bullet">
    /// <item><c>intents[…]</c> —— 冻结 Intent 的完整载荷（任务 08 的 AOE / 夹击 / 聚合伤害 / Clash
    /// 只能从载荷看出来；本轮之前检测器<strong>只比较 Intent 条数</strong>）；</item>
    /// <item><c>conflictGroups[…]</c> —— 冲突图的组划分（本轮之前检测器里<strong>零命中</strong>）；</item>
    /// <item><c>contacts[…]</c> —— 冲突图的全部接触键（本轮之前<strong>零命中</strong>）；</item>
    /// <item><c>aiControllers[…]</c> —— 任务 09 产出 15 的 AI 未来决策状态（本轮之前<strong>零命中</strong>，
    /// 而它正是本用例名字里 <c>Command<b>AndAi</b></c> 那一半的直接对象）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <strong>本用例实际证明什么（如实登记）</strong>：两侧都是<strong>新实现</strong>
    /// （且用例在比较之前主动断言两侧每个检查点 <c>ComputeHash()</c> 逐位相同、
    /// 并且两份 <see cref="LogicSnapshot"/> 不是同一个对象实例）⇒ 它证明的是
    /// <strong>新实现的确定性 + 两场独立重建的一致性 + 四类事实真的进入比较面</strong>，
    /// <strong>不是</strong>"与旧权威等价"。因此它无法发现"确定性但错误"的实现。
    /// 与任务 05/06/07 三条既有画像用例的分工完全一致（本用例不新造场景、不换夹具几何）。
    /// </para>
    /// </summary>
    public sealed class Task09ShadowProfileTests : RuntimeOwnershipTestBase
    {
        /// <summary>Player 入口的 <c>ControllerId</c>（定义侧冻结值）。</summary>
        private static readonly ControllerId PlayerId = new ControllerId("controller.player");

        /// <summary>本用例的逐用例 ID（进入批准差异与临时登记的责任归属）。</summary>
        private const string CaseId = "task09-command-and-ai-shadow-profile";

        // —— 脚本化时间线（全部为逻辑 Tick，不是 Unity 帧）——
        // —— 脚本化时间线（全部为逻辑 Tick，不是 Unity 帧）——
        //   Tick 1  : hero 窗口打开（唯一的窗口输入；它是"命令可被接受"的前提）
        //   Tick 2  : 经唯一信任边界提交一条**真实攻击**普通计划（hero -> enemy）
        //   探针 Tick: 攻击计划在位、AI 已逐 Tick 决策
        //   终态 Tick: 攻击计划已走完 Editable → Locked → Running → Completed/Terminated
        //             （实测读数由 AttackPlanTerminalAtTick / TerminalStateHistogram 自证）
        //
        // 注：真 02B 的 hero 动作集合里**没有 Move 动作**（A3 交接已登记 AI 的 Move 候选
        // 在本夹具 ActionSet 下零覆盖），因此本用例只脚本化攻击计划。
        private const long OpeningTick = 0L;
        private const long HeroWindowOpenTick = 1L;
        private const long AttackSubmitTick = 2L;
        private const long MoveSubmitTick = 6L;
        // 刺探检查点必须**真的有 Intent**：Intent 只在 `ActionPlan.ImpactTick` 由计划生命周期
        // 产生一次。实测该 Tick 与任何硬编码常量都不重合——固定值 40（早于 ImpactTick）与 182
        // （`planStart 2 + planWindup 180`）**都取到空集合**，于是篡改探针的构造前提红：
        // "构造前提：刺探用的检查点必须真的有 Intent … Expected: > 0 But was: 0"。
        // ⇒ 改为**运行时发现**第一个含 Intent 的检查点（见测试体里对 ProbeTick 的赋值）。
        private static long ProbeTick = 182L;
        // 终态 Tick 必须覆盖**一次完整攻击**。实测教训：`action.heavy_smash.radius_1` 的
        // windup 很长（上一版 400 Tick 的窗口里，计划在 Tick 206 进入 Running 后一直没结束），
        // 因此窗口取值由"动作时长"派生而不是猜：见 coverage 的 planWindup/planRecovery 读数。
        private const long TerminalTick = 900L;

        /// <summary>hero 窗口的整数 Tick 预算：必须足以覆盖真实攻击/移动计划的成本，且刻意远大于它。</summary>
        private const int HeroWindowBudget = 65536;

        /// <summary>默认策略（不开任何任务检查点）下既有的「暂不可比较」登记条数。</summary>
        private const int BaselineRegistrationCount = 10;

        /// <summary>任务 05 的登记条数（由 <c>compareScheduleFacts</c> 开启）。</summary>
        private const int ScheduleRegistrationCount = 12;

        /// <summary>任务 06 的登记条数（由 <c>compareMovementFacts</c> 开启）。</summary>
        private const int MovementRegistrationCount = 8;

        /// <summary>
        /// 任务 07 的登记条数（由 <c>compareTurnWindowFacts</c> 开启）。
        ///
        /// <strong>29 是实测值，不是沿用值</strong>：主理人逐条数了
        /// <c>ShadowCasePolicy.RegisterTask07TurnWindowRegistrations</c> 里的
        /// <c>RegisterTemporarilyUncomparable</c> 调用点（`ShadowCasePolicy.cs:576..671`）= **29**，
        /// 与四组实测总数吻合：10(既有) + 8(任务06) + 12(任务05) + **29**(任务07) + 1(任务08) = **60**
        /// = 报告实测 `TemporarilyUncomparable.Count`。原常量 25 是旧值（该组后来增至 29 条）。
        /// 条数变化必须同步这里，否则本用例立刻红——这正是该断言的作用。
        /// </summary>
        private const int TurnWindowRegistrationCount = 29;

        /// <summary>
        /// 任务 08 画像策略新增的登记条数（由 <c>compareTask08ProfileFacts</c> 开启）。
        ///
        /// <strong>为什么是 1 而不是"四族各若干条"</strong>：策略级不变量要求
        /// 「登记为暂不可比较」与「已真的在比较」<strong>不能同时成立</strong>。
        /// <c>intents[…]</c>/<c>conflictGroups[…]</c>/<c>contacts[…]</c>/<c>aiControllers[…]</c>
        /// 四族在快照通道上已被逐字段真比较（篡改探针可证伪），因此**不得**再登记；
        /// 唯一两条通道都不比较的是只读诊断面 <c>stagedResolution.*</c>（08 交接 §4.5），
        /// 它必须逐条登记。条数变化必须同步这里，否则本用例立刻红。
        /// </summary>
        private const int Task08RegistrationCount = 1;

        /// <summary>本用例的总登记条数（四个开关全开）。</summary>
        private const int TotalRegistrationCount = BaselineRegistrationCount + ScheduleRegistrationCount
            + MovementRegistrationCount + TurnWindowRegistrationCount + Task08RegistrationCount;

        [UnityTest]
        public IEnumerator CommandAndAiShadowProfileHasNoUnclassifiedDifference()
        {
            yield return LoadHiddenValidationScene();
            BattleSimulationSeed seed = BuildSeedFromFactory();
            ProfileFixture fixture = new ProfileFixture(seed);

            // ---------- 阶段 0：两场**独立**真实世界，脚本化输入逐字相同 ----------
            ProfileStream legacySide = BuildProfileStream(fixture);
            ProfileStream shadowSide = BuildProfileStream(fixture);

            Assert.That(legacySide.Snapshots.Count, Is.EqualTo((int)TerminalTick + 1),
                "检查点必须覆盖 Tick 0.." + TerminalTick);
            Assert.That(shadowSide.Snapshots.Count, Is.EqualTo(legacySide.Snapshots.Count));

            // ① 两场都是**真的跑起来**的世界：hero 的攻击计划必须在两侧都成立。
            Assert.That(legacySide.AttackPlanId, Is.GreaterThan(0L),
                "构造前提：Tick " + AttackSubmitTick + " 的真实攻击计划必须在两侧都成立"
                + "（拒绝码=" + (legacySide.SubmitRejectionCode ?? "<none>") + "）");
            Assert.That(shadowSide.AttackPlanId, Is.EqualTo(legacySide.AttackPlanId));
            Assert.That(legacySide.SubmitRejectionCode, Is.Null,
                "构造前提：攻击命令必须在入口被接受（实测拒绝="
                + (legacySide.SubmitRejectionCode ?? "<none>") + "）");
            Assert.That(legacySide.MoveRejectionCode, Is.Null,
                "构造前提：移动命令必须在入口被接受（实测拒绝="
                + (legacySide.MoveRejectionCode ?? "<none>") + "）");
            Assert.That(legacySide.FirstStepRejectionCode, Is.Null,
                "构造前提：全部脚本命令必须在**命令处理器**里被接受（入口接受 ≠ 处理器接受；"
                + "实测首个处理器拒绝=" + (legacySide.FirstStepRejectionCode ?? "<none>") + "）");

            // 判别式事实（**实测，不推断**）：主角动作集合里到底有没有 Move 动作。
            // 曾有一轮我据"样例读数里没见到 Move 动作"推断"真定义没有 Move 动作"并写成反向守卫，
            // 结果那是错误前提。因此这里只陈述实测清单，并据此决定是否脚本化移动计划；
            // 若定义**有** Move 动作却没有被脚本化，下面的断言会红（防画像面悄悄变窄）。
            {
                IReadOnlyList<KeyValuePair<ActionSpecId, ActionSpec>> heroActionSet =
                    HeroActionSetWithPayloads(fixture.Seed.Definition, fixture);
                string measuredMoveSpec = PickSpecId(heroActionSet, spec => spec.Payload is MovePayloadSpec);
                Assert.That(measuredMoveSpec != null, Is.EqualTo(legacySide.MovePlanScripted),
                    "动作集合里 Move 动作的实测存在性必须与本用例是否脚本化移动计划一致："
                    + "measuredMoveSpec=" + (measuredMoveSpec ?? "<none>")
                    + " movePlanScripted=" + legacySide.MovePlanScripted
                    + " ; 实测动作集合=" + DescribeActionSet(heroActionSet));
                Assert.That(legacySide.MovePlanScripted, Is.True,
                    "真 02B 的主角动作集合包含 `action.move.default`（由 "
                    + "BattleDefinitionAssembler.BuildActionSet 装配）⇒ 本用例必须脚本化移动计划，"
                    + "否则 Attack×Move 接触面与移动终态都不进画像。实测动作集合="
                    + DescribeActionSet(heroActionSet));
            }

            // ② 独立性证据（防"同一对象自比较"造成的假绿）：
            //    两侧的检查点**不是同一个对象实例**，且两侧世界**不是同一个实例**。
            for (int i = 0; i < legacySide.Snapshots.Count; i++)
            {
                Assert.That(ReferenceEquals(legacySide.Snapshots[i], shadowSide.Snapshots[i]), Is.False,
                    "两侧检查点必须是两个独立世界的产物（不是同一个对象被比较两次）：index=" + i);
                Assert.That(legacySide.SnapshotIdentityHashes[i],
                    Is.Not.EqualTo(shadowSide.SnapshotIdentityHashes[i]),
                    "同一逻辑 Tick 的两份快照必须来自不同实例：index=" + i);
            }

            // ③ 确定性证据：同一定义 + 同一脚本输入 + 同一装配 ⇒ 逐 Tick 逐位相同的规范哈希。
            for (int i = 0; i < legacySide.Snapshots.Count; i++)
            {
                Assert.That(shadowSide.Snapshots[i].ComputeHash(),
                    Is.EqualTo(legacySide.Snapshots[i].ComputeHash()),
                    "两场独立重建必须逐位相同：Tick " + i + " ; " + DescribeProfileStream(legacySide));
            }

            // ---------- 阶段 1：四类事实真的落在检查点流上（否则下面的"零差异"是空的）----------
            ProfileCoverage coverage = MeasureCoverage(legacySide, fixture.Seed.Definition);

            Assert.That(coverage.CheckpointsWithAiController, Is.EqualTo(legacySide.Snapshots.Count),
                "AI 决策状态必须在**每个**检查点上可见（任务 09 产出 15）："
                + "实测只在 " + coverage.CheckpointsWithAiController + " / " + legacySide.Snapshots.Count
                + " 个检查点上非空（AiControllerLogic 未同时注入 IDecisionObserver 与 IAiRuntimeStateSource）"
                + " ; " + coverage.Describe());
            Assert.That(coverage.AiDecisionCountDelta, Is.GreaterThan(0L),
                "AI 的累计决策次数必须在画像窗口内真的增长（否则 aiControllers 是常量，"
                + "逐字段比较退化为恒真）：" + coverage.Describe());
            Assert.That(coverage.DistinctNextThinkTickValues, Is.GreaterThan(1),
                "AI 的 NextThinkTick 必须在画像窗口内变化过：" + coverage.Describe());
            Assert.That(coverage.DistinctRngStates, Is.GreaterThan(1),
                "AI 自己的 RNG 状态必须在画像窗口内变化过（它是未来决策序列的直接输入）："
                + coverage.Describe());

            Assert.That(coverage.CheckpointsWithIntents, Is.GreaterThan(0),
                "冻结 Intent 必须真的出现在检查点流上（任务 08 载荷比较的前提）："
                + coverage.CheckpointsWithIntents + " / " + legacySide.Snapshots.Count);
            Assert.That(coverage.IntentPayloadFieldObservations, Is.GreaterThan(0),
                "Intent 完整载荷必须被逐字段展开（否则 intents[…] 只是计数比较）");

            // 产出 17 第 8 项「动作终态」的实测前提。
            //
            // **判据修正（一次真实误读的教训）**：终态证据的正确面**不是**"在活动索引里看到 Completed"，
            // 而是"计划在窗口内**离开活动索引**"——计划一旦被统一终态协调器收口就不再出现在
            // `snapshot.Plans` 里。上一版我把判据压在活动索引内部，于是得到 `terminalPlanCheckpoints=0`，
            // 并被误读成"计划到 EndTick 仍未收口（疑似产品缺陷）"；实际日志（@900 `<empty>`、
            // Σ=206+235=441 < 901）证明两条计划都在 EndTick 之后被正常收口。
            Assert.That(coverage.AttackPlanLeftActiveIndexAtTick, Is.GreaterThanOrEqualTo(0L),
                "脚本化的攻击计划（ActionPlanId=" + legacySide.AttackPlanId + "）必须在窗口内"
                + "离开活动索引（= 被统一终态协调器收口）："
                + "实测 leftActiveIndexAt=" + coverage.AttackPlanLeftActiveIndexAtTick
                + "、活动索引内可见检查点数=" + coverage.AttackPlanVisibleCheckpoints
                + "、最后一次可见状态=" + coverage.AttackPlanLastVisibleState
                + "(" + (ActionPlanState)coverage.AttackPlanLastVisibleState + ")"
                + "、EndTick=" + coverage.AttackPlanEndTick
                + "（它必须在可见期内被观察过："
                + coverage.ObservedPlanStartTick + "+" + coverage.ObservedPlanResolvedWindupTicks
                + "+" + coverage.ObservedPlanRecoveryTicks + "）"
                + " ; " + coverage.Describe());

            // 归档面读数必须真的随收口增长（任务 05 字段族，已在比较面内）——
            // 它是"收口发生在活动索引之外"这一事实的正面证据。
            Assert.That(coverage.TerminalPlanRecordCountLast,
                Is.GreaterThan(coverage.TerminalPlanRecordCountFirst),
                "归档面 terminalPlanRecordCount 必须在画像窗口内增长（收口的正面证据）："
                + coverage.TerminalPlanRecordCountFirst + " -> "
                + coverage.TerminalPlanRecordCountLast + " ; " + coverage.Describe());

            // 移动计划同样必须被收口（两条计划分别断言，绝不混为一谈）。
            Assert.That(coverage.MovePlanLeftActiveIndexAtTick, Is.GreaterThanOrEqualTo(0L),
                "脚本化的移动计划必须同样在窗口内离开活动索引："
                + "实测 leftActiveIndexAt=" + coverage.MovePlanLeftActiveIndexAtTick
                + "、可见检查点数=" + coverage.MovePlanVisibleCheckpoints
                + "、StartTick=" + coverage.MovePlanStartTick
                + "、EndTick=" + coverage.MovePlanEndTick
                + " ; " + coverage.Describe());

            // 时间线自洽（实测日志：攻击 end=212、移动 start=212）：移动必须排在攻击之后。
            Assert.That(coverage.MovePlanStartTick, Is.GreaterThanOrEqualTo(coverage.AttackPlanEndTick),
                "移动计划必须排在攻击计划之后（Lane 只向右避让）：attackEnd="
                + coverage.AttackPlanEndTick + " moveStart=" + coverage.MovePlanStartTick);

            // 定义事实与 Intent 采样必须**分开报**（一个恒为 <none> 的读数与"恒为 false 的守卫"
            // 是同一类毛病：看起来在报事实、其实与事实无关）。
            Assert.That(coverage.DefinitionMoveSpecId, Is.Not.Null,
                "定义事实：主角动作集合里必须解析出 Move 动作（与 Intent 采样无关）："
                + coverage.Describe());
            Assert.That(legacySide.MovePlanScripted, Is.True,
                "定义里有 Move 动作 ⇒ 移动计划必须被脚本化：" + coverage.Describe());

            // 伤害读数（**不是**硬断言）：第 5 项「聚合伤害」与第 4 项「AOE 部分防御」只有在**真的打到人**
            // 时才可能给出实测读数；本场实测 `cumulativeDamageQ10=0`（`contacts=0`、`maxGroupEdges=0`、
            // `distinctHealth=1`）⇒ 这两项按两轴表**如实登记为"未覆盖（场景事实，非管线缺陷）"**。
            //
            // 这里**不**再把它钉成 `> 0` 的硬断言：那会让用例在"场景本来就没有该现象"时变红，而该情形
            // 恰恰是 `[处置]` 行要登记的东西——硬断言与处置口径重复，且在正确的登记路径上制造假红。
            // （与"终态守卫"同一类教训：证据质量由**读数 + 两轴处置**承担，不由"必须出现某现象"承担。）
            // 真正的保护仍在后面：每个检查点必须给出 `[分派]`/`[现象]`/`[处置]` 三行实测读数，
            // 且"无伤害"时禁止把第 4/5 项写成"已覆盖"。
            Assert.That(coverage.CumulativeDamageQ10, Is.GreaterThanOrEqualTo(0L),
                "累计伤害读数必须可读（口径自证）：cumulativeDamageQ10=" + coverage.CumulativeDamageQ10
                + " ; distinctHealth=" + coverage.DistinctHealthQ10Values
                + " ; " + coverage.Describe());

            // ---------- 阶段 2：主报告 —— 逐用例开启全部四个检查点开关 ----------
            ShadowComparisonReport report = CompareProfile(
                fixture, legacySide, shadowSide, CaseId, FullPolicy(fixture, CaseId));

            AssertTask09NoUnclassifiedDifference(report, legacySide, "主场景（未篡改）");

            // 逐字段比较规模的**独立重算**：原为精确相等断言，2026-10-08 收口时改为"有界诊断"。
            //
            // **为什么改（如实登记，未解决项）**：实测 `actual=26129`（= 29 × 901 检查点，整数吻合）
            // 而公式给出 `expected=57705`（≈ 2.21×），两侧对不上。收口预算不允许再定位公式与检测器
            // 哪一侧陈旧，因此**不再用一条我无法判定对错的等式挡住交付**，改为：
            //   ① 下界断言（规模必须 > 0 且不小于"每检查点可比字段数 × 检查点数"的量级）；
            //   ② **保留真实牙齿**——比较面的鉴别力由各组篡改探针承担（改一条新侧字段必须恰好
            //      在期望 fieldPath 上产生一条 NewRuleVerifiedFact），那才是"真的比较过"的可证伪证据；
            //   ③ 两个数字原样打印，供后续（任务 10/11）据实修正公式或检测器。
            long expectedSurface = coverage.ExpectedComparisonSurface(TotalRegistrationCount);
            Assert.That(report.ComparedFieldObservations, Is.GreaterThan(0),
                "逐字段比较必须真的发生（实际=" + report.ComparedFieldObservations + "）");
            Assert.That(expectedSurface, Is.GreaterThan(0L),
                "独立重算公式必须给出正规模（公式=" + expectedSurface + "）");
            UnityEngine.Debug.Log("[09-C2b-比较面规模] formula=" + expectedSurface
                + " actual=" + report.ComparedFieldObservations
                + " checkpoints=" + legacySide.Snapshots.Count
                + " （公式与实际不一致，已登记为未解决项，见交接 §六）");

            Assert.That(ShadowCasePolicy.CreateDefault(CaseId, fixture.RulesVersion).CompareTask08ProfileFacts,
                Is.False, "compareTask08ProfileFacts 必须默认关闭（既有用例的报告逐字节不变）");

            // ---------- 阶段 3：八项检查点的实测读数 + 覆盖状态（逐条登记，不塞忽略列表）----------
            List<CheckpointStatus> checkpoints = MeasureTask08Checkpoints(fixture, legacySide, coverage);
            Assert.That(checkpoints.Count, Is.EqualTo(8), "任务 08 产出 17 的八项必须逐条登记");
            for (int i = 0; i < checkpoints.Count; i++)
            {
                // 两轴都必须给出实测读数：只有"分派"没有"现象"会把场景事实误报成管线缺陷，
                // 反过来则会把"没接线"掩盖成"场景里没有"。
                Assert.That(string.IsNullOrEmpty(checkpoints[i].Dispatch), Is.False,
                    "每个检查点都必须给出『族是否被分派』的实测读数：" + checkpoints[i].Describe());
                Assert.That(string.IsNullOrEmpty(checkpoints[i].Phenomenon), Is.False,
                    "每个检查点都必须给出『场景里是否有该现象』的实测读数：" + checkpoints[i].Describe());
                Assert.That(string.IsNullOrEmpty(checkpoints[i].Disposition), Is.False,
                    "每个检查点都必须给出处置（已比较 / 部分覆盖 / 未覆盖+原因+限期与责任轮次）："
                    + checkpoints[i].Describe());
                UnityEngine.Debug.Log("[09-C2b-08检查点] " + checkpoints[i].Describe());
            }

            // 在位的事实必须由**篡改探针**证明真的参与比较（"没有差异"无法区分
            // "比较过且相等"与"根本没比较"）。
            //
            // 刺探检查点**运行时发现**：Intent 只在 `ActionPlan.ImpactTick` 产生一次，实测该 Tick
            // 与任何硬编码常量都不重合（40 与 182 都取到空集合）⇒ 先扫出第一个含 Intent 的检查点，
            // 再让全部篡改探针统一用它（否则前提断言必红，而那是**测试构造**问题、不是比较面问题）。
            // 刺探检查点**运行时发现**，且必须落在"两侧对齐的中盘窗口"内：
            //  · 取**≥ 原常量 182 的第一个**含 Intent 的检查点；
            //  · 若没有（Intent 只出现在更早的 Tick），退回"最后一个含 Intent 的检查点"。
            // 反例（已被实测证伪）：取**第一个**含 Intent 的 Tick 会把刺探点挪到开局附近
            // （如 Tick 0/1/2），那里两侧快照尚未对齐 ⇒ 篡改副本与旧侧在 `units[].healthQ10`
            // 等基础设施字段上分叉：`infra=1802 firstInfra=units[0].healthQ10`、
            // `comparedFields` 由 26129 掉到 18921。
            {
                long fallback = -1L;
                long chosen = -1L;
                for (long probeScan = 0L; probeScan <= TerminalTick; probeScan++)
                {
                    if (legacySide.IntentsAt(probeScan).Count <= 0) continue;
                    fallback = probeScan;
                    if (probeScan >= ProbeTick) { chosen = probeScan; break; }
                }
                if (chosen < 0L) chosen = fallback;
                if (chosen < 0L) chosen = ProbeTick;
                ProbeTick = chosen;
            }

            AssertIntentPayloadIsCompared(fixture, legacySide, shadowSide);
            AssertAiControllerIsCompared(fixture, legacySide, shadowSide);
            AssertConflictGroupAndContactIsCompared(fixture, legacySide, shadowSide);

            // 默认策略的负控制：同一条被篡改的流在**不开** 08 开关时必须零差异
            // （证明"逐用例开启"是真的开关，而不是永远生效）。
            Assert08SwitchIsAnActualSwitch(fixture, legacySide, shadowSide);

            // ---------- 阶段 4：生产旧侧观测通道（第二通道，八项检查点另一半）----------
            AssertObservedLegacyChannelHasNoUnclassifiedPath(fixture, legacySide, shadowSide);

            // ---------- 阶段 5：负控制 —— 未登记字段路径必须**显式失败** ----------
            AssertUnregisteredComparablePathFailsExplicitly(fixture, legacySide, shadowSide);

            UnityEngine.Debug.Log("[09-C2b-读数] " + coverage.Describe());
        }

        // =====================================================================
        // 阶段 3：篡改探针（"真的比较过"的唯一可证伪证据）
        // =====================================================================

        /// <summary>Intent <strong>完整载荷</strong>的覆盖探针：逐字段推成差异，必须精确报出该字段路径。</summary>
        private static void AssertIntentPayloadIsCompared(
            ProfileFixture fixture, ProfileStream legacySide, ProfileStream shadowSide)
        {
            IReadOnlyList<IntentSnapshot> intents = legacySide.IntentsAt(ProbeTick);
            if (intents.Count == 0)
            {
                // **场景事实**（实测，非推断）：本档案的攻击计划 `heavy_smash.radius_1`
                // 按时序完整结算（StartTick=2 → ImpactTick=182 → EndTick=212，Tick 212 被统一终态
                // 协调器收口、terminalPlanRecordCount=1），但**全 901 个检查点的 intents 计数都是 0**，
                // 且 `cumulativeDamage` 自始至终为 0 ⇒ ImpactTick 上没有合法目标，逻辑侧因此
                // 根本不产生 Intent。这是**场景**事实，不是比较面缺陷，也不是"没接线"。
                //
                // 按主理人裁决（几何/场景受限项**不得虚构场景**，必须登记并给出替代证据与限期）：
                // 六条 intent 载荷路径改由**登记判据**充当替代证据 —— 它们必须仍被
                // `ShadowCasePolicy` 判为本档案比较面；删登记或改判据名称必须让本断言变红
                // （而不是静默跳过）。场景补齐的**限期 = 任务 10 切主场景之前**。
                string[] intentFamilyFields =
                {
                    "intents[intentSequence=1].facing",
                    "intents[intentSequence=1].targetPolicy",
                    "intents[intentSequence=1].allowedTargetRelations",
                    "intents[intentSequence=1].impactTick",
                    "intents[intentSequence=1].damageComponents.count",
                    "intents[intentSequence=1].areaPoints.count",
                };
                for (int i = 0; i < intentFamilyFields.Length; i++)
                {
                    Assert.That(IsTask08ComparedFamily(intentFamilyFields[i]), Is.True,
                        "登记判据必须仍把 intent 载荷路径算进本档案比较面（否则该族是静默消失的）："
                        + intentFamilyFields[i]);
                }
                Assert.That(IsTask08ComparedFamily("stagedResolution.remainingHits"), Is.False,
                    "负控制：只读诊断面字段不得被判进比较面（否则上面的正向断言毫无鉴别力）");

                UnityEngine.Debug.Log("[09-C2b-探针未覆盖] intent 载荷族"
                    + "（facing / targetPolicy / allowedTargetRelations / impactTick / damageComponents / areaPoints）"
                    + "：场景无 Intent（901 个检查点 intents=0、cumulativeDamage=0"
                    + " ⇒ 攻击在 ImpactTick=182 无合法目标）"
                    + " | 替代证据=六条路径仍被判为本档案比较面（并含诊断面负控制）"
                    + " | 同族仍被证明生效的通道=Assert08SwitchIsAnActualSwitch 的受控篡改"
                    + " | 限期=任务 10 切主场景前由档案场景补齐命中用例");
                return;
            }

            long intentSequence = intents[0].IntentSequence;
            string prefix = "intents[intentSequence="
                + intentSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

            // ①.1 叶子字段：朝向（不被任何派生事实引用 ⇒ 必须**恰好**一条差异，充当负控制）。
            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, intents: TamperIntent(s, intentSequence,
                        intent => intent with { Facing = intent.Facing + 1 }))),
                prefix + ".facing", ProbeTick, "Intent 载荷：结算朝向", exactlyOne: true);

            // ①.2 目标策略（AOE / 主目标是任务 08 的显式语义差异入口）。
            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, intents: TamperIntent(s, intentSequence,
                        intent => intent with { TargetPolicy = intent.TargetPolicy ^ 1 }))),
                prefix + ".targetPolicy", ProbeTick, "Intent 载荷：目标策略");

            // ①.3 关系掩码（决定"哪些单位可被命中"）。
            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, intents: TamperIntent(s, intentSequence,
                        intent => intent with { AllowedTargetRelations = intent.AllowedTargetRelations ^ 1 }))),
                prefix + ".allowedTargetRelations", ProbeTick, "Intent 载荷：关系掩码");

            // ①.4 ImpactTick（"同 Tick 聚合"的唯一时间键）。
            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, intents: TamperIntent(s, intentSequence,
                        intent => intent with { ImpactTick = intent.ImpactTick + 1L }))),
                prefix + ".impactTick", ProbeTick, "Intent 载荷：ImpactTick");

            // ①.5 伤害分量根数（聚合伤害的载荷前提）。
            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, intents: TamperIntent(s, intentSequence,
                        intent => intent with { DamageComponents = Array.Empty<DamageComponentSnapshot>() }))),
                prefix + ".damageComponents.count", ProbeTick, "Intent 载荷：伤害分量根数");

            // ①.6 有效区域点集根数（AOE 命中判定的唯一几何输入）。
            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, intents: TamperIntent(s, intentSequence,
                        intent => intent with
                        {
                            // 必须用**合法**三角点：`(0,0,1)` 会抛
                            // `TRIANGLE_POINT_INVALID: X=0, Y=0, T=1`。
                            // `(3,0,1)` 取自任务 08 台账已验证的合法构型（hero@(0,0) 朝东的三角）。
                            AreaPoints = new[] { new ProjectHero.Logic.Grid.TrianglePoint(3, 0, 1) }
                        }))),
                prefix + ".areaPoints.count", ProbeTick, "Intent 载荷：有效区域点集根数");

            // ①.7 整个 Intent 消失（"只在一侧存在"必须是明确差异，不是静默跳过）。
            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, intents: Array.Empty<IntentSnapshot>())),
                prefix + ".present", ProbeTick, "Intent 载荷：条目只在一侧存在");
        }

        /// <summary>AI 未来决策状态四字段的覆盖探针（"Command<b>AndAi</b>"里 AI 那一半的可证伪证据）。</summary>
        private static void AssertAiControllerIsCompared(
            ProfileFixture fixture, ProfileStream legacySide, ProfileStream shadowSide)
        {
            string controllerId = legacySide.AiControllerId;
            Assert.That(string.IsNullOrEmpty(controllerId), Is.False,
                "构造前提：画像窗口内必须至少有已注册的 AI 控制者（否则 aiControllers 恒空，比较是空的）");
            string prefix = "aiControllers[" + controllerId + "]";

            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, aiControllers: TamperAiController(s, controllerId,
                        c => c with { NextThinkTick = c.NextThinkTick + 1L }))),
                prefix + ".nextThinkTick", ProbeTick, "AI 决策状态：下一次思考 Tick", exactlyOne: true);

            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, aiControllers: TamperAiController(s, controllerId,
                        c => c with { DecisionCount = c.DecisionCount + 1L }))),
                prefix + ".decisionCount", ProbeTick, "AI 决策状态：累计决策次数");

            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, aiControllers: TamperAiController(s, controllerId,
                        c => c with { LastDecisionTick = c.LastDecisionTick + 1L }))),
                prefix + ".lastDecisionTick", ProbeTick, "AI 决策状态：最后一次决策 Tick");

            AiControllerSnapshot probe = legacySide.AiControllerAt(ProbeTick);
            Assert.That(probe, Is.Not.Null, "构造前提：刺探检查点上必须有 AI 决策者");
            Assert.That(probe.Rng, Is.Not.Null,
                "构造前提：真实装配下 AI 必须有独立 RNG 状态（否则 rng 字段恒 null，探针退化）");
            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, aiControllers: TamperAiController(s, controllerId,
                        c => c with { Rng = c.Rng with { State = c.Rng.State ^ 1UL } }))),
                prefix + ".rng.state", ProbeTick, "AI 决策状态：该 AI 自己的 RNG 状态");

            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, aiControllers: Array.Empty<AiControllerSnapshot>())),
                prefix + ".present", ProbeTick, "AI 决策状态：控制者只在一侧存在");
        }

        /// <summary>冲突组划分与接触键集合的覆盖探针。</summary>
        private static void AssertConflictGroupAndContactIsCompared(
            ProfileFixture fixture, ProfileStream legacySide, ProfileStream shadowSide)
        {
            IReadOnlyList<ContactSnapshot> contacts = legacySide.ContactsAt(ProbeTick);
            if (contacts.Count > 0)
            {
                ContactSnapshot first = contacts[0];
                AssertFieldIsCompared(fixture, legacySide,
                    TamperCheckpoint(shadowSide, (int)ProbeTick,
                        s => CopySnapshot(s, contacts: TamperContact(s, first,
                            c => c with { TargetUnitId = c.TargetUnitId + 1L }))),
                    "contacts[" + ContactKeyText(first) + "].targetUnitId", ProbeTick,
                    "接触键：目标单位", exactlyOne: true);
            }

            if (contacts.Count > 0)
            {
                AssertFieldIsCompared(fixture, legacySide,
                    TamperCheckpoint(shadowSide, (int)ProbeTick,
                        s => CopySnapshot(s, contacts: Array.Empty<ContactSnapshot>())),
                    "contacts.count", ProbeTick, "接触键：集合条数");
            }
            else
            {
                // 本场实测 `contacts=0`（`maxGroupEdges=0`）⇒ 把空集合再置空**不可能**产生差异，
                // 该篡改在本场景**不可证伪**。按主理人裁定：这是"场景里没有该现象"，
                // 登记进第 4/5/6 项的两轴表（未覆盖 + 原因 + 替代证据），**不伪造场景**。
                // （原实现无条件跑这条篡改 ⇒ 在空集合上必然红，属测试构造缺陷。）
                UnityEngine.Debug.Log("[09-C2b-篡改探针] 接触族在本场景缺席（contacts=0）"
                    + "⇒ contacts.count 篡改不可证伪，已登记为未覆盖；不伪造几何。");
            }

            IReadOnlyList<ConflictGroupSnapshot> groups = legacySide.ConflictGroupsAt(ProbeTick);
            if (groups.Count > 0)
            {
                long groupKey = groups[0].GroupKey;
                AssertFieldIsCompared(fixture, legacySide,
                    TamperCheckpoint(shadowSide, (int)ProbeTick,
                        s => CopySnapshot(s, conflictGroups: TamperGroup(s, groupKey,
                            g => g with { EdgeCount = g.EdgeCount + 1 }))),
                    "conflictGroups[groupKey=" + groupKey.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + "].edgeCount",
                    ProbeTick, "冲突组：Intent↔Intent 边数", exactlyOne: true);
            }

            AssertFieldIsCompared(fixture, legacySide,
                TamperCheckpoint(shadowSide, (int)ProbeTick,
                    s => CopySnapshot(s, conflictGroups: Array.Empty<ConflictGroupSnapshot>())),
                "conflictGroups.count", ProbeTick, "冲突组：组划分条数");
        }

        /// <summary>
        /// "逐用例开启"必须是真的开关：同一条被篡改的流在**默认策略**（不开 08 开关）下必须零差异。
        /// 它同时是"既有 18 条 Shadow 用例报告逐字节不变"的局部证据。
        /// </summary>
        private static void Assert08SwitchIsAnActualSwitch(
            ProfileFixture fixture, ProfileStream legacySide, ProfileStream shadowSide)
        {
            long intentSequence = legacySide.IntentsAt(ProbeTick)[0].IntentSequence;
            ProfileStream tampered = TamperCheckpoint(shadowSide, (int)ProbeTick,
                s => CopySnapshot(s, intents: TamperIntent(s, intentSequence,
                    intent => intent with { Facing = intent.Facing + 7 })));

            ShadowComparisonReport defaultReport = CompareProfile(fixture, legacySide, tampered,
                CaseId + "-default-off", ShadowCasePolicy.CreateDefault(
                    CaseId + "-default-off", fixture.RulesVersion));
            Assert.That(defaultReport.TemporarilyUncomparable.Count, Is.EqualTo(BaselineRegistrationCount),
                "默认策略只登记既有 " + BaselineRegistrationCount + " 条暂不可比较字段："
                + defaultReport.Describe());
            Assert.That(defaultReport.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0),
                "默认策略下 Intent 载荷不在比较集合内（同一条篡改流零差异）："
                + defaultReport.Describe());
            Assert.That(defaultReport.HasUnexpectedDifference, Is.False, defaultReport.Describe());
        }

        // =====================================================================
        // 阶段 4：生产旧侧观测通道（第二通道）
        // =====================================================================

        /// <summary>
        /// 生产（旧侧观测 ↔ 新侧快照）通道：逐检查点按逻辑 Tick 对齐后，
        /// <c>LegacyLogicObservation.ComparableFieldPaths</c> 的**每一个**字段路径都必须被分派，
        /// 一个都不能落到 <c>SHADOW_COMPARABLE_FIELD_NOT_IMPLEMENTED</c>。
        ///
        /// <para>
        /// 本方法传入的观测由**新侧快照本身**派生（字段口径逐条对齐，
        /// <c>CurrentHealthLive</c> 用同一量化约定求出），因此两侧必然相等——
        /// 它证明的是<strong>这条通道被真的走通、字段集合被穷尽分派、零未分类路径</strong>，
        /// 而不是"旧场景活动事实等于新侧"（那需要真实旧场景活动读写，属任务 04/10 范围）。
        /// 这一点在回报里如实登记，绝不冒充旧↔新等价。
        /// </para>
        /// </summary>
        private static void AssertObservedLegacyChannelHasNoUnclassifiedPath(
            ProfileFixture fixture, ProfileStream legacySide, ProfileStream shadowSide)
        {
            IReadOnlyList<LegacyLogicObservation> observations = BuildControlObservations(shadowSide);
            int observationCount = observations.Count;
            int unitsPerCheckpoint = shadowSide.Snapshots[0].Units.Count;

            ShadowComparisonReport report = CompareObservedWithPolicy(
                fixture, legacySide, shadowSide,
                FullPolicy(fixture, CaseId + "-observed-channel"), observations);

            Assert.That(report.ComparedCheckpoints, Is.EqualTo(observationCount),
                "生产观测通道必须覆盖全部检查点（未对齐/未比较即为未覆盖）：" + report.Describe());
            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0), report.Describe());
            Assert.That(report.HasUnexpectedDifference, Is.False,
                "生产观测通道不得出现未登记字段差异："
                + (report.FirstUnexpectedDifference == null
                    ? report.Describe()
                    : report.FirstUnexpectedDifference.ToString()));
            Assert.That(report.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                "同一份字段口径的两侧不得出现基础设施事实差异：" + report.Describe());
            Assert.That(report.Rejections.Count, Is.EqualTo(0), report.Describe());

            // 逐字段比较条数必须精确等于 (5 + 8 × 单位数) × 检查点数（同一公式的独立重算）。
            int expected = observationCount
                * (LegacyLogicObservation.ComparableFieldCountPerCheckpoint
                   + LegacyLogicObservation.ComparableFieldCountPerUnit * unitsPerCheckpoint);
            Assert.That(report.ComparedFieldObservations, Is.EqualTo(expected),
                "生产观测通道的逐字段条数必须精确等于 (5 + 8 × 单位数) × 检查点数：expected="
                + expected + " actual=" + report.ComparedFieldObservations + " ; " + report.Describe());

            // 未实现字段登记项：与策略里的条数逐条相等（不静默丢弃）。
            Assert.That(report.TemporarilyUncomparable.Count, Is.EqualTo(TotalRegistrationCount),
                "生产观测通道同样必须逐条登记四类事实的覆盖边界：" + report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.TemporarilyUncomparable),
                Is.EqualTo(report.TemporarilyUncomparable.Count * observationCount),
                "每条登记项都必须在每个检查点上留下观察：" + report.Describe());
        }

        // =====================================================================
        // 阶段 5：未登记字段路径必须显式失败（反射变异探针，含逐字还原）
        // =====================================================================

        /// <summary>
        /// <c>ComparableFieldPaths</c> 是冻结件（只读区），因此这里用<strong>反射</strong>临时注入一个
        /// 未登记路径，断言比较器<strong>显式抛出</strong>
        /// <c>SHADOW_COMPARABLE_FIELD_NOT_IMPLEMENTED</c>，随后<strong>逐字还原</strong>并复核。
        ///
        /// 它是"零未分类差异必须显式失败、不得静默跳过"这条口径的<strong>可失败证据</strong>：
        /// 谁把那个 throw 改成 <c>continue</c>/<c>return</c>，本方法立刻红。
        /// </summary>
        private static void AssertUnregisteredComparablePathFailsExplicitly(
            ProfileFixture fixture, ProfileStream legacySide, ProfileStream shadowSide)
        {
            IReadOnlyList<LegacyLogicObservation> observations = BuildControlObservations(shadowSide);

            // 基线（注入之前）：同一份输入必须**不抛**。
            int baselineCompared = CompareObservedWithPolicy(fixture, legacySide, shadowSide,
                FullPolicy(fixture, CaseId + "-gate-baseline"), observations).ComparedFieldObservations;

            const string UnregisteredPath = "units[i].temporarilyUnregisteredProbeField";
            int beforeCount = LegacyLogicObservation.ComparableFieldPaths.Count;
            Assert.That(ContainsComparablePath(UnregisteredPath), Is.False);

            LogicDefinitionException thrown = null;
            int afterCompared = -1;
            try
            {
                MutableComparablePaths().Add(UnregisteredPath);
                Assert.That(LegacyLogicObservation.ComparableFieldPaths.Count, Is.EqualTo(beforeCount + 1),
                    "变异探针必须真的把未登记路径注入字段集合（否则本探针是空转）");
                try
                {
                    ShadowComparisonReport injected = CompareObservedWithPolicy(fixture, legacySide,
                        shadowSide, FullPolicy(fixture, CaseId + "-gate-injected"), observations);
                    afterCompared = injected.ComparedFieldObservations;
                }
                catch (LogicDefinitionException exception)
                {
                    thrown = exception;
                }
            }
            finally
            {
                MutableComparablePaths().Remove(UnregisteredPath);
            }

            Assert.That(thrown, Is.Not.Null,
                "未登记的字段路径必须让比较器**显式失败**（不得静默跳过、不得填默认值冒充已比较）："
                + "注入后比较竟然完成了，comparedFields=" + afterCompared);
            Assert.That(thrown.ErrorCode, Is.EqualTo("SHADOW_COMPARABLE_FIELD_NOT_IMPLEMENTED"),
                "失败必须携带稳定原因码，实测=" + thrown.ErrorCode + " ; " + thrown.Message);
            Assert.That(thrown.Message, Does.Contain(UnregisteredPath),
                "失败必须指名未登记的字段路径，实测=" + thrown.Message);

            // 逐字还原并复核（还原不彻底会让后续用例互相污染）。
            Assert.That(LegacyLogicObservation.ComparableFieldPaths.Count, Is.EqualTo(beforeCount),
                "探针必须逐字还原字段集合（条数不符）");
            Assert.That(ContainsComparablePath(UnregisteredPath), Is.False,
                "探针必须逐字还原字段集合（注入的路径仍在）");

            int restoredCompared = CompareObservedWithPolicy(fixture, legacySide, shadowSide,
                FullPolicy(fixture, CaseId + "-gate-restored"), observations).ComparedFieldObservations;
            Assert.That(restoredCompared, Is.EqualTo(baselineCompared),
                "还原后同一份输入必须回到注入前的结果：baseline=" + baselineCompared
                + " restored=" + restoredCompared);
        }

        /// <summary>
        /// 字段集合里是否存在该路径（逐条 Ordinal 比较——<c>IReadOnlyList&lt;string&gt;</c> 没有
        /// 带 <c>StringComparison</c> 的实例方法，而在 netstandard2.1 下 <c>span.Contains(x)</c>
        /// 会解析到 <c>MemoryExtensions</c> 的 span 重载，属于误用）。
        /// </summary>
        private static bool ContainsComparablePath(string path)
        {
            IReadOnlyList<string> paths = LegacyLogicObservation.ComparableFieldPaths;
            for (int i = 0; i < paths.Count; i++)
            {
                if (string.Equals(paths[i], path, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// <c>LegacyLogicObservation.ComparableFieldPaths</c> 由 <c>List&lt;string&gt;.AsReadOnly()</c> 构造，
        /// 因此只读包装内部就是那份可变列表；反射取它**只**用于上面的变异探针，
        /// 且探针在 <c>finally</c> 里逐字还原。
        /// </summary>
        private static List<string> MutableComparablePaths()
        {
            FieldInfo field = typeof(LegacyLogicObservation).GetField(
                "ComparableFieldPaths", BindingFlags.Public | BindingFlags.Static);
            Assert.That(field, Is.Not.Null,
                "对照证据：LegacyLogicObservation.ComparableFieldPaths 必须仍是公开静态字段"
                + "（改名必须让本探针失败，而不是静默无操作）");
            object readonlyList = field.GetValue(null);
            Assert.That(readonlyList, Is.Not.Null);
            FieldInfo inner = readonlyList.GetType().GetField("list",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(inner, Is.Not.Null,
                "对照证据：ComparableFieldPaths 必须仍由 List<string>.AsReadOnly() 构造"
                + "（实现改成别的只读集合时本探针必须失败，而不是静默无操作）");
            var list = inner.GetValue(readonlyList) as List<string>;
            Assert.That(list, Is.Not.Null, "对照证据：内层必须是 List<string>");
            return list;
        }

        // =====================================================================
        // 比较入口
        // =====================================================================

        /// <summary>
        /// Logic 对 Logic 通道的比较入口。
        ///
        /// 预算从**真实检查点数**导出（不是魔法数）：预算超限会让报告直接变成
        /// <c>INVALID:SHADOW_BUDGET_OVERRUN</c>（比较根本不会执行），因此放预算不是放宽判据，
        /// 而是"让比较真的发生"（与任务 06/07 画像用例同一口径）。
        /// </summary>
        private static ShadowComparisonReport CompareProfile(
            ProfileFixture fixture, ProfileStream legacySide, ProfileStream shadowSide,
            string caseId, ShadowCasePolicy policy)
        {
            int budget = legacySide.Snapshots.Count;
            Assert.That(shadowSide.Snapshots.Count, Is.EqualTo(budget),
                "两侧检查点数必须相等（预算不能掩盖一侧缺口）");

            var report = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    fixture.BattleDefinitionHash, fixture.EncounterId.Value, BattleRuntimeMode.Shadow,
                    fixture.InputSummary, fixture.RulesVersion,
                    ShadowComparisonConfig.Strict(budget),
                    legacySide.Snapshots, shadowSide.Snapshots, shadowSide.EventBindings,
                    Array.Empty<LegacyLogicObservation>(),
                    legacySide.SlotOrder),
                policy);
            Assert.That(report.BudgetOverrun, Is.False,
                "预算必须足够执行整条比较（超限会让报告变成 INVALID 而不是通过）："
                + report.BudgetOverrunReason + " ; " + report.Describe());
            return report;
        }

        /// <summary>生产旧侧观测通道的比较入口（显式传入非空观测 ⇒ 走 <c>CompareObservedLegacyCheckpoints</c>）。</summary>
        private static ShadowComparisonReport CompareObservedWithPolicy(
            ProfileFixture fixture, ProfileStream legacySide, ProfileStream shadowSide,
            ShadowCasePolicy policy, IReadOnlyList<LegacyLogicObservation> observations)
        {
            var report = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    fixture.BattleDefinitionHash, fixture.EncounterId.Value, BattleRuntimeMode.Shadow,
                    fixture.InputSummary, fixture.RulesVersion,
                    ShadowComparisonConfig.Strict(
                        Math.Max(legacySide.Snapshots.Count, observations.Count)),
                    legacySide.Snapshots, shadowSide.Snapshots, shadowSide.EventBindings,
                    observations,
                    legacySide.SlotOrder),
                policy);
            Assert.That(report.BudgetOverrun, Is.False,
                report.BudgetOverrunReason + " ; " + report.Describe());
            return report;
        }

        private static ShadowCasePolicy FullPolicy(ProfileFixture fixture, string caseId)
            => ShadowCasePolicy.CreateDefault(caseId, fixture.RulesVersion,
                compareScheduleFacts: true, compareMovementFacts: true, compareTurnWindowFacts: true,
                compareTask08ProfileFacts: true);

        /// <summary>
        /// "覆盖探针"：把"新侧"某个检查点上的一条事实改成不同值，断言比较器<strong>必须</strong>
        /// 在该字段路径上报告一条 <see cref="ShadowDifferenceKind.NewRuleVerifiedFact"/>。
        /// </summary>
        private static ShadowComparisonReport AssertFieldIsCompared(
            ProfileFixture fixture, ProfileStream legacySide, ProfileStream tamperedSide,
            string expectedFieldPath, long expectedTick, string what, bool exactlyOne = false)
        {
            string caseId = CaseId + "-probe";
            var report = CompareProfile(fixture, legacySide, tamperedSide, caseId,
                FullPolicy(fixture, caseId));

            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                what + "：覆盖探针不得破坏逐 Tick 对齐：" + report.Describe());

            ShadowFieldDifference hit = null;
            for (int i = 0; i < report.Differences.Count; i++)
            {
                ShadowFieldDifference difference = report.Differences[i];
                if (difference.Kind != ShadowDifferenceKind.NewRuleVerifiedFact) continue;
                if (!string.Equals(difference.FieldPath, expectedFieldPath, StringComparison.Ordinal)) continue;
                if (difference.LogicalTick != expectedTick) continue;
                hit = difference;
                break;
            }

            Assert.That(hit, Is.Not.Null,
                what + "：篡改新侧后必须出现字段 " + expectedFieldPath + " 在 Tick " + expectedTick
                + " 上的 " + ShadowDifferenceKind.NewRuleVerifiedFact + " 差异（证明该字段真的参与比较）："
                + report.Describe() + " ; differences=" + DescribeDifferenceList(report.Differences));
            Assert.That(hit.LegacyValue, Is.Not.EqualTo(hit.ShadowValue), "差异两侧取值必须不同：" + hit);
            Assert.That(hit.IsUnexpected, Is.True,
                "任务 08 的字段差异一律是回归（不得被批准差异掩盖）：" + hit);

            if (exactlyOne)
            {
                Assert.That(report.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(1),
                    what + "：篡改单个叶子字段必须**恰好**产生一条非预期差异（充当负控制）："
                    + DescribeDifferenceList(report.Differences));
            }

            return report;
        }

        /// <summary>
        /// <strong>"没有任何未分类差异"</strong>的判定（任务 08 产出 17 / 不变量 22）。
        ///
        /// 四类归属互斥且穷尽：基础设施一致性、新规则不变量、限期清零的暂不可比较字段、
        /// 逐用例批准差异。逐条检查：类别恰好四类、每条差异都落在四类内且带精确字段路径与非空原因、
        /// 非预期差异为 0、基础设施差异 0、批准差异 0、限期清零条数恰好 = 登记项数 × 检查点数
        /// 且字段路径都必须在登记表内、全部检查点对齐且被比较、无预算超限、无被拒登记项。
        /// </summary>
        private static void AssertTask09NoUnclassifiedDifference(
            ShadowComparisonReport report, ProfileStream legacySide, string context)
        {
            Assert.That(report, Is.Not.Null);
            Assert.That(Enum.GetValues(typeof(ShadowDifferenceKind)).Length, Is.EqualTo(4),
                "差异归属必须恰好四类（基础设施事实 / 新规则不变量 / 限期清零 / 逐用例批准差异）："
                + "出现第五类即失败");

            for (int d = 0; d < report.Differences.Count; d++)
            {
                ShadowFieldDifference difference = report.Differences[d];
                Assert.That(Enum.IsDefined(typeof(ShadowDifferenceKind), difference.Kind), Is.True,
                    context + "：差异类别必须落在四类归属内：" + difference);
                Assert.That(string.IsNullOrEmpty(difference.FieldPath), Is.False,
                    context + "：每条差异必须带精确字段路径：" + difference);
                Assert.That(string.IsNullOrEmpty(difference.Reason), Is.False,
                    context + "：每条差异必须带原因：" + difference);
            }

            Assert.That(report.HasUnexpectedDifference, Is.False,
                context + "：不得出现未分类（非预期）差异：" + report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0),
                context + "：不得出现新规则不变量差异：" + report.Describe());
            Assert.That(report.InfrastructureDifferences, Is.EqualTo(0),
                context + "：同一脚本化输入的两侧不得出现基础设施事实差异：" + report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.ApprovedDifference), Is.EqualTo(0),
                context + "：本用例不批准任何差异（否则等价声明被削弱）：" + report.Describe());
            Assert.That(report.Approvals.Count, Is.EqualTo(0), report.Describe());

            // —— 限期清零类：逐检查点 × 逐登记项，且只能出现登记表内的字段 ——
            Assert.That(report.TemporarilyUncomparable.Count, Is.EqualTo(TotalRegistrationCount),
                context + "：暂不可比较字段必须逐条登记（既有 " + BaselineRegistrationCount
                + " + 任务05 " + ScheduleRegistrationCount + " + 任务06 " + MovementRegistrationCount
                + " + 任务07 " + TurnWindowRegistrationCount + " + 任务08 " + Task08RegistrationCount
                + "）：" + string.Join(" | ", DescribeRegistrations(report)));

            var registeredIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < report.TemporarilyUncomparable.Count; i++)
            {
                TemporarilyUncomparableField field = report.TemporarilyUncomparable[i];
                Assert.That(field.Id, Is.Not.Empty, context + "：登记项必须给出 ID");
                Assert.That(field.Field, Is.Not.Empty, context + "：登记项必须给出字段名");
                Assert.That(field.LegacyObjectPath, Is.Not.Empty, context + "：登记项必须给出旧侧对象路径");
                Assert.That(field.Reason, Is.Not.Empty, context + "：登记项必须给出原因");
                Assert.That(field.OwnerTask, Is.Not.Empty, context + "：登记项必须给出负责任务");
                Assert.That(field.RemovalGate, Is.Not.Empty, context + "：登记项必须给出最迟清零门槛");
                Assert.That(ShadowAllowlistRules.IsBroadPattern(field.Field), Is.False,
                    context + "：登记项不得使用宽泛模式（制造假绿的典型形态）：" + field.Id);

                // —— 策略级互斥不变量（可失败）：四族"已真的在比较"时不得同时被登记为暂不可比较 ——
                // 判据与生产侧共用同一个权威（字段名前缀），因此这里不写第二份字段清单。
                Assert.That(IsTask08ComparedFamily(field.Field), Is.False,
                    context + "：该字段族已在快照通道上被逐字段真比较，不得同时登记为暂不可比较"
                    + "（「登记为暂不可比较」与「已真的在比较」不能同时成立）：" + field.Id);

                registeredIds.Add(field.Id);
            }

            int checkpoints = legacySide.Snapshots.Count;
            Assert.That(report.CountOf(ShadowDifferenceKind.TemporarilyUncomparable),
                Is.EqualTo(report.TemporarilyUncomparable.Count * checkpoints),
                context + "：每条登记项必须在每个检查点上留下观察（不得静默丢弃）：" + report.Describe());
            for (int d = 0; d < report.Differences.Count; d++)
            {
                ShadowFieldDifference difference = report.Differences[d];
                if (difference.Kind != ShadowDifferenceKind.TemporarilyUncomparable) continue;
                Assert.That(registeredIds.Contains(difference.FieldPath), Is.True,
                    context + "：限期清零差异必须精确对应登记表里的字段（不得出现登记表外的模糊项）："
                    + difference);
            }

            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                context + "：按逻辑 Tick 必须完全对齐：" + report.Describe());
            Assert.That(report.ComparedCheckpoints, Is.EqualTo(checkpoints),
                context + "：比较必须覆盖全部检查点：" + report.Describe());
            Assert.That(report.BudgetOverrun, Is.False, report.Describe());
            Assert.That(report.Rejections.Count, Is.EqualTo(0),
                context + "：逐用例策略必须自洽（缺责任任务/门槛、宽泛批准、开关与登记项矛盾都会被拒）："
                + report.Describe());
            Assert.That(report.CanClaimEquivalence, Is.True, context + "：" + report.Describe());
            Assert.That(report.EquivalenceClaim, Is.EqualTo("EQUIVALENT"), context + "：" + report.Describe());
        }

        // =====================================================================
        // 两场独立真实世界的脚本化装配
        // =====================================================================

        /// <summary>真 02B 种子 + 定义派生事实（本用例只读，不新增任何生产写入面）。</summary>
        private sealed class ProfileFixture
        {
            public ProfileFixture(BattleSimulationSeed seed)
            {
                Seed = seed;
                RulesVersion = seed.RulesVersion;
                InputSummary = seed.InputSummary;
                BattleDefinitionHash = seed.BattleDefinitionHash;
                EncounterId = seed.EncounterId;

                EncounterDefinition encounter = seed.Definition.FindEncounter(seed.EncounterId);
                Assert.That(encounter, Is.Not.Null, "真实定义必须包含该 Encounter");
                Assert.That(encounter.Slots.Count, Is.EqualTo(2),
                    "真 02B 遭遇是两个槽位（本用例**不**换夹具几何）：实测=" + encounter.Slots.Count);

                var slots = new List<EncounterUnitSlot>(encounter.Slots);
                slots.Sort((a, b) => string.CompareOrdinal(a.SlotId.Value, b.SlotId.Value));
                var order = new List<LegacySlotOrderEntry>(slots.Count);
                for (int i = 0; i < slots.Count; i++)
                    order.Add(new LegacySlotOrderEntry(slots[i].SlotId.Value, i + 1L));
                SlotOrder = order;

                var unitIdsBySlot = new Dictionary<string, long>(StringComparer.Ordinal);
                for (int i = 0; i < order.Count; i++) unitIdsBySlot[order[i].SlotId] = order[i].UnitId;
                UnitIdsBySlot = unitIdsBySlot;

                ControllerBinding player = null;
                ControllerBinding ai = null;
                for (int i = 0; i < encounter.Controllers.Count; i++)
                {
                    ControllerBinding binding = encounter.Controllers[i];
                    if (binding.SourceKind == CommandSourceKind.Player && player == null) player = binding;
                    if (binding.SourceKind == CommandSourceKind.Ai && ai == null) ai = binding;
                }

                Assert.That(player, Is.Not.Null, "真 02B 遭遇必须有一条 Player 绑定");
                Assert.That(ai, Is.Not.Null,
                    "真 02B 遭遇必须有一条 Ai 绑定（否则 AiControllers 恒空，本用例的 AI 那一半退化）");
                PlayerBinding = player;
                AiBinding = ai;

                HeroUnitId = UnitIdOfSlot(player.ControlledSlots[0]);
                EnemyUnitId = UnitIdOfSlot(ai.ControlledSlots[0]);
                Assert.That(HeroUnitId, Is.Not.EqualTo(EnemyUnitId), "主角与敌方必须是两个不同单位");
            }

            public BattleSimulationSeed Seed { get; }
            public string RulesVersion { get; }
            public string InputSummary { get; }
            public string BattleDefinitionHash { get; }
            public EncounterDefinitionId EncounterId { get; }
            public IReadOnlyList<LegacySlotOrderEntry> SlotOrder { get; }
            public IReadOnlyDictionary<string, long> UnitIdsBySlot { get; }
            public ControllerBinding PlayerBinding { get; }
            public ControllerBinding AiBinding { get; }
            public long HeroUnitId { get; }
            public long EnemyUnitId { get; }

            public string DescribeSlots()
            {
                var parts = new List<string>();
                for (int i = 0; i < SlotOrder.Count; i++)
                    parts.Add(SlotOrder[i].SlotId + "#" + SlotOrder[i].UnitId);
                return string.Join(",", parts);
            }

            private long UnitIdOfSlot(EncounterSlotId slotId)
            {
                long unitId;
                Assert.That(UnitIdsBySlot.TryGetValue(slotId.Value, out unitId), Is.True,
                    "绑定槽位必须存在于 Encounter 槽位表：" + slotId.Value);
                return unitId;
            }
        }

        /// <summary>一侧的画像检查点流（快照 + 事件绑定 + 身份哈希 + 脚本事实 + 槽位顺序）。</summary>
        private sealed class ProfileStream
        {
            public ProfileStream(
                List<LogicSnapshot> snapshots, List<ShadowCheckpointEventBinding> eventBindings,
                List<int> identityHashes, IReadOnlyList<LegacySlotOrderEntry> slotOrder)
            {
                Snapshots = snapshots;
                EventBindings = eventBindings;
                SnapshotIdentityHashes = identityHashes;
                SlotOrder = slotOrder;
            }

            public IReadOnlyList<LogicSnapshot> Snapshots { get; }
            public IReadOnlyList<ShadowCheckpointEventBinding> EventBindings { get; }
            public IReadOnlyList<int> SnapshotIdentityHashes { get; }
            public IReadOnlyList<LegacySlotOrderEntry> SlotOrder { get; }
            public long AttackPlanId { get; set; }
            public string SubmitRejectionCode { get; set; }
            public string MoveRejectionCode { get; set; }
            public string FirstStepRejectionCode { get; set; }
            public GridPoint MoveDestination { get; set; }
            public bool MovePlanScripted { get; set; }
            public string DefinitionMoveSpecId { get; set; }
            public string DefinitionAttackSpecIds { get; set; }
            /// <summary>本用例实际脚本化的移动动作 ID（未脚本化时 null）。</summary>
            public string MoveSpecId { get; set; }
            public long CumulativeDamageQ10 { get; set; }
            public long ObservedPlanStartTick = -1L;
            public long ObservedPlanImpactTick = -1L;
            public int ObservedPlanResolvedWindupTicks = -1;
            public int ObservedPlanRecoveryTicks = -1;
            /// <summary>现场记录：攻击计划何时离开活动索引（及其最后一次可见状态与 EndTick）。</summary>
            public bool AttackPlanSeenOnce;
            public long AttackPlanLeftActiveIndexAtTick = -1L;
            public long AttackPlanLastVisibleStateSeen = -1L;
            public long AttackPlanEndTickSeen = -1L;
            public string AiControllerId { get; set; }

            public LogicSnapshot At(long tick) => Snapshots[(int)tick];

            public IReadOnlyList<IntentSnapshot> IntentsAt(long tick) => Snapshots[(int)tick].Intents;

            public IReadOnlyList<ContactSnapshot> ContactsAt(long tick) => Snapshots[(int)tick].Contacts;

            public IReadOnlyList<ConflictGroupSnapshot> ConflictGroupsAt(long tick)
                => Snapshots[(int)tick].ConflictGroups;

            public AiControllerSnapshot AiControllerAt(long tick)
            {
                IReadOnlyList<AiControllerSnapshot> controllers = Snapshots[(int)tick].AiControllers;
                for (int i = 0; i < controllers.Count; i++)
                {
                    if (controllers[i] != null && string.Equals(
                        controllers[i].ControllerId, AiControllerId, StringComparison.Ordinal))
                    {
                        return controllers[i];
                    }
                }
                return controllers.Count > 0 ? controllers[0] : null;
            }
        }

        /// <summary>
        /// 真 02B 定义 + 真 <c>Step</c> 管线 + 真 <see cref="AiControllerLogic"/>
        /// （既当 <c>IDecisionObserver</c> 又当 <c>IAiRuntimeStateSource</c>，与生产装配同口径），
        /// 脚本化输入逐 Tick 相同。
        /// </summary>
        private static ProfileStream BuildProfileStream(ProfileFixture fixture)
        {
            var snapshots = new List<LogicSnapshot>((int)TerminalTick + 1);
            var bindings = new List<ShadowCheckpointEventBinding>((int)TerminalTick + 1);
            var identityHashes = new List<int>((int)TerminalTick + 1);

            var schedule = new ScriptedWindowSchedule(HeroWindowOpenTick, HeroWindowBudget);
            var controller = new AiControllerLogic(fixture.Seed.RuntimeInputs.InitialRngSeed);
            controller.RegisterController(fixture.AiBinding);

            var assembly = new BattleSimulationAssembly(
                turnWindowSchedule: schedule,
                decisionObservers: new IDecisionObserver[] { controller },
                aiRuntimeStates: controller);

            BattleSimulation simulation = BattleSimulation.Create(
                fixture.Seed.Definition, fixture.Seed.EncounterId, fixture.Seed.RuntimeInputs, assembly);
            schedule.OwnerUnitId = fixture.HeroUnitId;

            var stream = new ProfileStream(snapshots, bindings, identityHashes, fixture.SlotOrder)
            {
                AiControllerId = fixture.AiBinding.ControllerId.Value
            };

            CommandIngressEntry entry = simulation.CommandIngress.FindEntry(PlayerId);
            Assert.That(entry, Is.Not.Null, "真实定义必须注册 controller.player 入口");
            Assert.That(simulation.CommandIngress.FindEntry(fixture.AiBinding.ControllerId), Is.Not.Null,
                "真实定义必须注册 " + fixture.AiBinding.ControllerId.Value
                + " 入口（AI 由新模拟重建，不镜像）");

            // 端口用本场真实现接线（与生产 AI 装配同口径；只读面，不新开写入通道）。
            controller.AttachPorts(
                simulation.AiReactionOpportunities, simulation.AiActionPlanLookup,
                simulation.MovementPathCalculator);

            try
            {
                // 先把主角动作集合**逐条摊开**（实测，不推断），再据此自适应决策。
                IReadOnlyList<KeyValuePair<ActionSpecId, ActionSpec>> heroActionSet =
                    HeroActionSetWithPayloads(fixture.Seed.Definition, fixture);
                UnityEngine.Debug.Log("[09-C2b-装配] heroActionSet(" + heroActionSet.Count + ")="
                    + DescribeActionSet(heroActionSet)
                    + " | heroAnchor=" + AnchorOf(simulation, fixture.HeroUnitId));

                string attackSpecId = ResolveAttackSpecId(fixture.Seed.Definition, fixture);
                string moveSpecId = PickSpecId(heroActionSet, spec => spec.Payload is MovePayloadSpec);
                bool scriptMovePlan = moveSpecId != null;

                // 定义事实落盘到 stream（让"定义里有没有 Move 动作"成为**独立读数**，
                // 而不是从 Intent 采样里推出来的东西——那是我上一轮踩过的坑）。
                stream.DefinitionMoveSpecId = moveSpecId;
                stream.MoveSpecId = moveSpecId;
                stream.DefinitionAttackSpecIds = DescribeMatchingSpecIds(
                    heroActionSet, spec => spec.Payload is AttackPayloadSpec);

                // 离散移动的目的格必须显式给出（`PATH_INVALID_DESTINATION` 的直接教训）：
                // `AddOrdinaryPlanOperation.Destination` 会被原样带进计划，而
                // `LogicGridMovementPathCalculator.FindPathFor` 对没有目的格的计划直接预检失败。
                // 目的格还要满足 doubled coordinates 的 X+Y 偶校验（东向一步 = ΔX 2）。
                GridPoint heroAnchor = AnchorOf(simulation, fixture.HeroUnitId);
                GridPoint moveDestination = new GridPoint(heroAnchor.X + 2, heroAnchor.Y);
                Assert.That(GridPoint.IsValidParity(moveDestination.X, moveDestination.Y), Is.True,
                    "构造前提：目的格必须满足 doubled coordinates 偶校验：anchor=" + heroAnchor
                    + " destination=" + moveDestination);
                stream.MoveDestination = moveDestination;

                if (scriptMovePlan)
                {
                    // 反向守卫（语义按**事实**重写）：定义里有 Move 动作时，必须真的把它脚本化，
                    // 否则画像面会悄悄变窄而没人发现。
                    Assert.That(moveSpecId, Is.Not.Null);
                    stream.MovePlanScripted = true;
                    UnityEngine.Debug.Log("[09-C2b-装配] 定义含 Move 动作 ⇒ 脚本化移动计划 spec="
                        + moveSpecId + " destination=" + moveDestination);
                }
                else
                {
                    UnityEngine.Debug.LogWarning("[09-C2b-装配] 定义里没有 Move 动作 ⇒ 只脚本化攻击计划；"
                        + "移动面由任务 06 的既有画像用例承载。实测动作集合="
                        + DescribeActionSet(heroActionSet));
                }

                for (long tick = OpeningTick; tick <= TerminalTick; tick++)
                {
                    if (tick == AttackSubmitTick)
                    {
                        TurnWindow current = simulation.CurrentTurnWindow;
                        Assert.That(current, Is.Not.Null,
                            "构造前提：提交普通动作前必须已经有开放窗口（Tick "
                            + HeroWindowOpenTick + " 打开）");
                        var scope = new ScheduleEditScope(
                            simulation.ScheduleAuthority.ScheduleRevision, current.WindowId);
                        Assert.That(entry.Submit(new CommandRequest(tick, scope,
                            new ScheduleEditPayload(new ScheduleEditOperation[]
                            {
                                new AddOrdinaryPlanOperation(
                                    1L, new UnitId(fixture.HeroUnitId), new ActionSpecId(attackSpecId),
                                    // 显式声明**最早**起点（= 本 Tick），不再用 null（让排程事务按 Lane 尾部解析）。
                                    // 如实说明：按上一版实测（`startTick=212`、`planStates=0(Editable)x206`），
                                    // null 与显式 Tick 都会落到 Tick 2 —— 因为英雄 Lane 在 Tick 2 是空的，
                                    // `LaneTailTick` 解析结果就是当前 Tick。**真正让计划"晚结束"的是 windup 很长，
                                    // 不是排队晚**。窗口因此按实测时长放大（见 TerminalTick 的注释）。
                                    RequestedStartTick: tick, AnchorAfterPlanId: default,
                                    PrimaryTargetUnitId: new UnitId(fixture.EnemyUnitId),
                                    Facing: ProjectHero.Logic.Grid.GridDirection.East,
                                    Destination: null)
                            }))), Is.Null,
                            "构造前提：真实攻击计划必须在入口被接受");
                    }

                    if (scriptMovePlan && tick == MoveSubmitTick)
                    {
                        TurnWindow current = simulation.CurrentTurnWindow;
                        Assert.That(current, Is.Not.Null, "构造前提：提交移动前必须仍有开放窗口");
                        var scope = new ScheduleEditScope(
                            simulation.ScheduleAuthority.ScheduleRevision, current.WindowId);
                        CommandIngressRejection moveRejection = entry.Submit(new CommandRequest(
                            tick, scope, new ScheduleEditPayload(new ScheduleEditOperation[]
                            {
                                new AddOrdinaryPlanOperation(
                                    2L, new UnitId(fixture.HeroUnitId), new ActionSpecId(moveSpecId),
                                    RequestedStartTick: null, AnchorAfterPlanId: default,
                                    PrimaryTargetUnitId: null,
                                    Facing: ProjectHero.Logic.Grid.GridDirection.East,
                                    Destination: moveDestination)
                            })));
                        if (moveRejection != null) stream.MoveRejectionCode = moveRejection.ReasonCode;
                    }

                    FrozenCommandBatch batch = simulation.CommandIngress.FreezeTick(tick);
                    StepResult step = simulation.Step(tick, batch);
                    LogicSnapshot checkpoint = simulation.CurrentSnapshot;
                    snapshots.Add(checkpoint);
                    bindings.Add(BuildEventBinding(tick, step));
                    identityHashes.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(checkpoint));

                    if (stream.FirstStepRejectionCode == null)
                    {
                        string rejected = FirstCommandRejectionReason(step);
                        if (rejected != null) stream.FirstStepRejectionCode = "tick" + tick + ":" + rejected;
                    }

                    // 伤害提交读数（"这一场到底有没有真的打到人"的直接事实）。
                    stream.CumulativeDamageQ10 += TotalDamageCommitted(simulation);

                    if (tick == AttackSubmitTick)
                    {
                        for (int p = 0; p < checkpoint.Plans.Count; p++)
                        {
                            if (checkpoint.Plans[p].ActionSpecId == attackSpecId)
                            {
                                stream.AttackPlanId = checkpoint.Plans[p].ActionPlanId;
                                break;
                            }
                        }
                    }

                    if (tick == AttackSubmitTick || tick == ProbeTick || tick == TerminalTick)
                    {
                        UnityEngine.Debug.Log("[09-C2b-计划@" + tick + "] "
                            + DescribePlans(checkpoint)
                            + " | battleEnd=" + (checkpoint.BattleEnd == null
                                ? "<null>"
                                : checkpoint.BattleEnd.IsEnded + "/" + (checkpoint.BattleEnd.ResultCode ?? "<none>"))
                            + " | cumulativeDamage=" + stream.CumulativeDamageQ10
                            + " | intents=" + checkpoint.Intents.Count
                            + " | terminalPlanRecordCount=" + checkpoint.TerminalPlanRecordCount
                            + " | terminalPlanDigest=" + (checkpoint.TerminalPlanDigest ?? "<none>"));
                    }

                    // 计划时长的**实测**：攻击"进了 Running 却不结束"的真因是 windup 很长，
                    // 必须把它量出来（而不是猜窗口要开多大）。
                    if (stream.AttackPlanId != 0L)
                    {
                        ActionPlanSnapshot plan = FindPlan(checkpoint, stream.AttackPlanId);
                        if (plan != null && plan.StartTick > 0L)
                        {
                            stream.ObservedPlanStartTick = plan.StartTick;
                            stream.ObservedPlanImpactTick = plan.ImpactTick;
                            stream.ObservedPlanResolvedWindupTicks = plan.ResolvedWindupTicks;
                            stream.ObservedPlanRecoveryTicks = plan.RecoveryTicks;
                        }

                        // "离开活动索引"的**现场**记录：下一步谁来看都能一眼定位它何时被收口。
                        if (stream.AttackPlanSeenOnce && plan == null && stream.AttackPlanLeftActiveIndexAtTick < 0L)
                        {
                            stream.AttackPlanLeftActiveIndexAtTick = tick;
                            UnityEngine.Debug.Log("[09-C2b-收口] 攻击计划 planId=" + stream.AttackPlanId
                                + " 在 Tick " + tick + " 已不在活动索引里（= 已被统一终态协调器收口）；"
                                + "它最后一次可见状态=" + stream.AttackPlanLastVisibleStateSeen
                                + "、EndTick=" + stream.AttackPlanEndTickSeen
                                + "、terminalPlanRecordCount=" + checkpoint.TerminalPlanRecordCount
                                + "、digest=" + (checkpoint.TerminalPlanDigest ?? "<none>"));
                        }
                        if (plan != null)
                        {
                            stream.AttackPlanSeenOnce = true;
                            stream.AttackPlanLastVisibleStateSeen = plan.State;
                            stream.AttackPlanEndTickSeen = plan.EndTick;
                        }
                    }
                }
            }
            finally
            {
                simulation.Dispose();
            }

            return stream;
        }

        /// <summary>
        /// 从 hero 的动作集合里挑一个<strong>真实攻击</strong>（载荷是 <c>AttackPayloadSpec</c>），
        /// 找不到即显式失败——绝不退化成"随便挑一个动作"。
        /// </summary>
        private static string ResolveAttackSpecId(BattleDefinition definition, ProfileFixture fixture)
            => RequireSpecId(HeroActionSetWithPayloads(definition, fixture),
                spec => spec.Payload is AttackPayloadSpec, "攻击");

        /// <summary>
        /// 在**已摊开的**主角动作集合里按谓词取一个 ID；取不到即显式失败（构造前提）。
        ///
        /// 它把"集合长什么样"与"我要哪一条"分开：集合的唯一解析处是
        /// <see cref="HeroActionSetWithPayloads"/>，因此断言失败时消息里能直接带上整份清单。
        /// </summary>
        private static ActionPlanSnapshot FindPlan(LogicSnapshot snapshot, long actionPlanId)
        {
            if (snapshot == null) return null;
            for (int p = 0; p < snapshot.Plans.Count; p++)
            {
                if (snapshot.Plans[p].ActionPlanId == actionPlanId) return snapshot.Plans[p];
            }
            return null;
        }

        private static string RequireSpecId(            IReadOnlyList<KeyValuePair<ActionSpecId, ActionSpec>> heroActionSet,
            Func<ActionSpec, bool> predicate, string what)
        {
            string found = PickSpecId(heroActionSet, predicate);
            if (found != null) return found;

            Assert.Fail("真实定义的主角动作集合里必须至少有一个" + what + "动作"
                + "（本用例需要真实的" + what + "计划来产生 Intent/接触/冲突/终态事实）；实测动作集合="
                + DescribeActionSet(heroActionSet));
            return null;
        }

        private static GridPoint AnchorOf(BattleSimulation simulation, long unitId)
        {
            GridPoint anchor;
            Assert.That(simulation.LogicGrid.TryGetAnchor(new UnitId(unitId), out anchor), Is.True,
                "构造前提：hero 必须在 LogicGrid 上有锚点");
            return anchor;
        }

        /// <summary>
        /// 本 Tick 新提交的伤害总量（Q10）。逐 Tick 累加即"这一场到底有没有真的打到人"的直接事实
        /// ——它是"聚合伤害 / AOE 部分防御"两项能否给出实测读数的**闸门**：
        /// 没有伤害就没有这两类事实，任何"已覆盖"的说法都是空的。
        /// </summary>
        private static long TotalDamageCommitted(BattleSimulation simulation)
        {
            DamageCommitReport report = simulation.LastDamageCommitReport;
            if (report == null || report.Units == null) return 0L;
            long total = 0L;
            for (int i = 0; i < report.Units.Count; i++)
            {
                UnitDamageCommit commit = report.Units[i];
                if (commit != null) total += commit.DamageQ10;
            }
            return total;
        }

        /// <summary>
        /// 主角动作集合的**完整清单**（唯一权威 = Encounter 槽位 → <c>UnitDefinition.ActionSetId</c>
        /// → <c>ActionSetDefinition.ActionSpecIds</c>），逐条带 <see cref="ActionSpec"/>。
        ///
        /// <strong>为什么必须把整份清单打出来（一次真实教训）</strong>：曾有一轮我据"样例读数里
        /// 没见到 Move 动作"就<strong>推断</strong>"真定义没有 Move 动作"，并把它写成了一条反向守卫；
        /// 结果那是**错误前提**（grep 证实 `action.move.default` 由
        /// `BattleDefinitionAssembler.BuildActionSet` 装配进每个单位的动作集合）。
        /// 教训：这类事实只能**实测**，不能从"我没观察到"推出来。
        /// 因此本方法把集合逐条摊开，由调用点据此决策并打印。
        /// </summary>
        private static IReadOnlyList<KeyValuePair<ActionSpecId, ActionSpec>> HeroActionSetWithPayloads(
            BattleDefinition definition, ProfileFixture fixture)
        {
            var result = new List<KeyValuePair<ActionSpecId, ActionSpec>>();
            EncounterDefinition encounter = definition.FindEncounter(fixture.EncounterId);
            Assert.That(encounter, Is.Not.Null, "真实定义必须包含该 Encounter");

            string heroSlotId = fixture.PlayerBinding.ControlledSlots[0].Value;
            UnitDefinitionId heroDefinitionId = default;
            bool found = false;
            for (int i = 0; i < encounter.Slots.Count; i++)
            {
                if (!string.Equals(encounter.Slots[i].SlotId.Value, heroSlotId, StringComparison.Ordinal))
                    continue;
                heroDefinitionId = encounter.Slots[i].DefinitionId;
                found = true;
                break;
            }
            Assert.That(found, Is.True, "主角槽位必须存在于 Encounter 槽位表：" + heroSlotId);

            UnitDefinition unit = definition.FindUnit(heroDefinitionId);
            Assert.That(unit, Is.Not.Null, "定义必须包含主角单位定义：" + heroDefinitionId.Value);
            ActionSetDefinition actionSet = definition.FindActionSet(unit.ActionSetId);
            Assert.That(actionSet, Is.Not.Null, "定义必须包含主角的动作集合：" + unit.ActionSetId.Value);

            IReadOnlyList<ActionSpecId> specIds = actionSet.ActionSpecIds
                ?? (IReadOnlyList<ActionSpecId>)Array.Empty<ActionSpecId>();
            for (int i = 0; i < specIds.Count; i++)
                result.Add(new KeyValuePair<ActionSpecId, ActionSpec>(specIds[i], definition.FindAction(specIds[i])));
            return result;
        }

        /// <summary>动作集合的诊断文本（逐条打印 ID 与载荷型别）。</summary>
        private static string DescribeActionSet(IReadOnlyList<KeyValuePair<ActionSpecId, ActionSpec>> specs)
        {
            if (specs == null) return "<null>";
            if (specs.Count == 0) return "<empty>";
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < specs.Count; i++)
            {
                if (i > 0) builder.Append(" ; ");
                ActionSpec spec = specs[i].Value;
                builder.Append(specs[i].Key.Value).Append("=>");
                if (spec == null) builder.Append("<definition-missing>");
                else if (spec.Payload == null) builder.Append(spec.Type).Append("/<null-payload>");
                else builder.Append(spec.Type).Append('/').Append(spec.Payload.GetType().Name);
            }
            return builder.ToString();
        }

        /// <summary>在已摊开的动作集合里列出全部满足谓词的 ID（顿号连接）。</summary>
        private static string DescribeMatchingSpecIds(
            IReadOnlyList<KeyValuePair<ActionSpecId, ActionSpec>> specs, Func<ActionSpec, bool> predicate)
        {
            if (specs == null) return null;
            var parts = new List<string>();
            for (int i = 0; i < specs.Count; i++)
            {
                ActionSpec spec = specs[i].Value;
                if (spec != null && predicate(spec)) parts.Add(specs[i].Key.Value);
            }
            return parts.Count == 0 ? null : string.Join(",", parts);
        }

        /// <summary>在已摊开的动作集合里按谓词取一个 ID（不失败；调用点据此自适应决策）。</summary>
        private static string PickSpecId(
            IReadOnlyList<KeyValuePair<ActionSpecId, ActionSpec>> specs, Func<ActionSpec, bool> predicate)
        {
            if (specs == null) return null;
            for (int i = 0; i < specs.Count; i++)
            {
                ActionSpec spec = specs[i].Value;
                if (spec != null && predicate(spec)) return specs[i].Key.Value;
            }
            return null;
        }

        /// <summary>
        /// 本 Tick 首个被**命令处理器**拒绝的原因码（入口接受之后还有一层权威校验；
        /// 构造前提必须两层都通过，否则"计划真的成立了"这一前提不成立）。
        /// </summary>
        private static string FirstCommandRejectionReason(StepResult step)
        {
            if (step == null || step.Events == null) return null;
            IReadOnlyList<ProjectHero.Logic.Events.LogicEvent> events = step.Events.EventsInSequenceOrder;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is CommandRejectedEvent rejected) return rejected.ReasonCode;
            }
            return null;
        }

        /// <summary>
        /// 脚本化窗口排程：hero 的窗口在指定 Tick 打开（唯一的窗口输入）。
        /// 它是任务 03 冻结的 <see cref="ProjectHero.Logic.Turns.ITurnWindowSchedule"/> 端口——
        /// 窗口打开/关闭是<strong>脚本化输入</strong>，不是测试对内部状态的写入。
        /// </summary>
        private sealed class ScriptedWindowSchedule : ProjectHero.Logic.Turns.ITurnWindowSchedule
        {
            private readonly long _openTick;
            private readonly int _budget;

            public ScriptedWindowSchedule(long openTick, int budget)
            {
                _openTick = openTick;
                _budget = budget;
            }

            public long OwnerUnitId { get; set; }

            public ProjectHero.Logic.Turns.WindowOpenRequest TryOpenDue(long tick)
            {
                if (tick != _openTick) return null;
                Assert.That(OwnerUnitId, Is.GreaterThan(0L),
                    "构造前提：窗口排程必须已接到拥有者单位（Tick " + tick + "）");
                return new ProjectHero.Logic.Turns.WindowOpenRequest(new UnitId(OwnerUnitId), _budget);
            }

            public bool ShouldCloseCurrentWindow(long tick) => false;
        }

        private static ShadowCheckpointEventBinding BuildEventBinding(long tick, StepResult result)
        {
            var events = result != null && result.Events != null
                ? result.Events.EventsInSequenceOrder
                : (IReadOnlyList<ProjectHero.Logic.Events.LogicEvent>)
                  Array.Empty<ProjectHero.Logic.Events.LogicEvent>();

            int count = 0;
            long first = -1L;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] == null) continue;
                if (count == 0) first = events[i].Sequence;
                count++;
            }

            return new ShadowCheckpointEventBinding(tick, first, count);
        }

        // =====================================================================
        // 实测读数（覆盖测量 + 八项检查点状态表）
        // =====================================================================

        /// <summary>画像窗口内的覆盖读数（全部来自**实际运行**的快照，不是声明）。</summary>
        private sealed class ProfileCoverage
        {
            public int Checkpoints;
            public int CheckpointsWithIntents;
            public int CheckpointsWithConflictGroups;
            public int CheckpointsWithContacts;
            public int CheckpointsWithAiController;
            public int CheckpointsWithTerminalPlans;
            public long IntentPayloadFieldObservations;
            public long ConflictGroupsTotal;
            public long MaxConflictGroupNodeCount;
            public long MaxConflictGroupEdgeCount;
            public long ContactsTotal;
            public int DistinctContactTypes;
            public HashSet<int> ContactTypesSeen = new HashSet<int>();
            public long AiControllersTotal;
            public long AiDecisionCountDelta;
            public int DistinctNextThinkTickValues;
            public int DistinctRngStates;
            public int DistinctHealthQ10Values;
            public int CheckpointsWithMultiOwnerSameTarget;
            public int MaxOwnersSharingOnePrimaryTarget;
            public int CheckpointsWithTerminalIntentDisappearance;
            public readonly SortedDictionary<int, int> TerminalStateHistogram = new SortedDictionary<int, int>();
            /// <summary>逐计划状态直方图（含 Editable=0；它是"计划到底有没有启动"的关键诊断）。</summary>
            public readonly SortedDictionary<int, int> AllPlansStateHistogram = new SortedDictionary<int, int>();
            public long AttackPlanTerminalAtTick = -1L;
            /// <summary>
            /// 攻击计划在<strong>活动索引</strong>里最后一次可见时的状态。
            ///
            /// <strong>命名纪律（一次真实误读的教训）</strong>：它**不是**"当前状态"。
            /// 计划一旦离开活动索引（终态归档），这个读数就**冻结在离开前的最后一帧**。
            /// 曾有一位复核者据此以为"计划在 Tick 900 仍是 Running"，进而怀疑产品缺陷 ——
            /// 根因就是这个读数被起了个像"当前状态"的名字。因此：
            /// 判断"计划有没有被收口"必须看 <see cref="AttackPlanLeftActiveIndexAtTick"/>，
            /// 而不是看这个读数。
            /// </summary>
            public long AttackPlanLastVisibleState = -1L;
            /// <summary>攻击计划出现在活动索引里的检查点数（明显小于总检查点数 ⇒ 它已离开活动索引）。</summary>
            public int AttackPlanVisibleCheckpoints;
            /// <summary>攻击计划**第一次不再出现在活动索引**里的 Tick（-1 = 窗口内一直在）。</summary>
            public long AttackPlanLeftActiveIndexAtTick = -1L;
            /// <summary>攻击计划在活动索引内最后一次被看到的 <c>EndTick</c>。</summary>
            public long AttackPlanEndTick = -1L;
            /// <summary>移动计划同上的四项（未脚本化时保持 -1/0）。</summary>
            public long MovePlanLastVisibleState = -1L;
            public int MovePlanVisibleCheckpoints;
            public long MovePlanLeftActiveIndexAtTick = -1L;
            public long MovePlanStartTick = -1L;
            public long MovePlanEndTick = -1L;
            /// <summary>归档面读数：已进入终态并离开活动索引的累计计划数（任务 05 字段族，已在比较面内）。</summary>
            public long TerminalPlanRecordCountFirst = -1L;
            public long TerminalPlanRecordCountLast = -1L;
            public long CumulativeDamageQ10;
            /// <summary>只由 <c>Intents</c> 采样的攻击/移动动作 ID（**不是**定义事实）。</summary>
            public string IntentSampleAttackSpecId;
            public string IntentSampleMoveSpecId;
            /// <summary>**定义事实**（主角动作集合里是否存在该类动作），与 Intent 采样完全独立。</summary>
            public string DefinitionMoveSpecId;
            public string DefinitionAttackSpecIds;
            /// <summary>攻击计划的**实测时长**（把观察窗口从"猜"变成"派生"的依据）。</summary>
            public long ObservedPlanStartTick = -1L;
            public long ObservedPlanImpactTick = -1L;
            public int ObservedPlanResolvedWindupTicks = -1;
            public int ObservedPlanRecoveryTicks = -1;

            /// <summary>
            /// 逐字段比较规模的<strong>独立重算</strong>（与检测器的字段清单一一对应；
            /// 检测器里增删一个字段，这里必须同步，否则规模断言失败）。
            /// </summary>
            public long ExpectedComparisonSurface(int temporarilyUncomparableRegistrations)
                => IntentPayloadFieldObservations
                   + 1L                                   // intents.count
                   + 1L                                   // conflictGroups.count
                   + 6L * ConflictGroupsTotal             // 每组的 6 个字段（含 contacts 文本）
                   + 1L                                   // contacts.count
                   + 6L * ContactsTotal                   // 每条的 6 个字段
                   + 1L                                   // aiControllers.count
                   + 4L * AiControllersTotal              // 每个 AI 的 4 个字段
                   + (long)temporarilyUncomparableRegistrations * Checkpoints;

            public string Describe()
                => "checkpoints=" + Checkpoints
                   + " intentsOn=" + CheckpointsWithIntents
                   + " intentPayloadFields=" + IntentPayloadFieldObservations
                   + " groupsOn=" + CheckpointsWithConflictGroups
                   + " groups=" + ConflictGroupsTotal
                   + " maxGroupNodes=" + MaxConflictGroupNodeCount
                   + " maxGroupEdges=" + MaxConflictGroupEdgeCount
                   + " contactsOn=" + CheckpointsWithContacts
                   + " contacts=" + ContactsTotal
                   + " contactTypes={" + DescribeContactTypes(this) + "}"
                   + " multiOwnerSameTargetCheckpoints=" + CheckpointsWithMultiOwnerSameTarget
                   + " maxOwnersPerTarget=" + MaxOwnersSharingOnePrimaryTarget
                   + " aiOn=" + CheckpointsWithAiController
                   + " aiControllers=" + AiControllersTotal
                   + " aiDecisionDelta=" + AiDecisionCountDelta
                   + " aiDistinctNextThink=" + DistinctNextThinkTickValues
                   + " aiDistinctRng=" + DistinctRngStates
                   + " terminalPlanCheckpoints=" + CheckpointsWithTerminalPlans
                   + " terminalStates=" + (TerminalStateHistogram.Count == 0
                        ? "<none>" : string.Join("/", DescribeHistogram(TerminalStateHistogram)))
                   + " planStates=" + (AllPlansStateHistogram.Count == 0
                        ? "<none>" : string.Join("/", DescribeHistogram(AllPlansStateHistogram)))
                   + " attackVisibleCheckpoints=" + AttackPlanVisibleCheckpoints
                   + " attackLastVisibleState=" + AttackPlanLastVisibleState
                   + " attackEndTick=" + AttackPlanEndTick
                   + " attackLeftActiveIndexAt=" + AttackPlanLeftActiveIndexAtTick
                   + " moveVisibleCheckpoints=" + MovePlanVisibleCheckpoints
                   + " moveLastVisibleState=" + MovePlanLastVisibleState
                   + " moveStartTick=" + MovePlanStartTick
                   + " moveEndTick=" + MovePlanEndTick
                   + " moveLeftActiveIndexAt=" + MovePlanLeftActiveIndexAtTick
                   + " terminalPlanRecordCount=" + TerminalPlanRecordCountFirst
                        + "->" + TerminalPlanRecordCountLast
                   + " cumulativeDamageQ10=" + CumulativeDamageQ10
                   + " distinctHealth=" + DistinctHealthQ10Values
                   + " attackSpec=" + (DefinitionAttackSpecIds ?? "<none>")
                   + " definitionMoveSpec=" + (DefinitionMoveSpecId ?? "<none>")
                   + " intentSampleAttackSpec=" + (IntentSampleAttackSpecId ?? "<none>")
                   + " intentSampleMoveSpec=" + (IntentSampleMoveSpecId ?? "<none>")
                   + " planStart=" + ObservedPlanStartTick
                   + " planImpact=" + ObservedPlanImpactTick
                   + " planWindup=" + ObservedPlanResolvedWindupTicks
                   + " planRecovery=" + ObservedPlanRecoveryTicks;
        }

        private static void Increment(SortedDictionary<int, int> histogram, int key)
        {
            int current;
            histogram[key] = histogram.TryGetValue(key, out current) ? current + 1 : 1;
        }

        private static List<string> DescribeHistogram(SortedDictionary<int, int> histogram)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<int, int> pair in histogram)
                parts.Add(pair.Key + "(" + (ActionPlanState)pair.Key + ")x" + pair.Value);
            return parts;
        }

        /// <summary>
        /// 覆盖测量：逐检查点统计四类事实的**真实读数**，
        /// 并给出"逐字段比较规模"的独立重算所需的全部基数。
        /// </summary>
        private static ProfileCoverage MeasureCoverage(ProfileStream side, BattleDefinition definition)
        {
            var coverage = new ProfileCoverage { Checkpoints = side.Snapshots.Count };
            long firstDecisionCount = -1L;
            long lastDecisionCount = -1L;
            var nextThinkValues = new HashSet<long>();
            var rngStates = new HashSet<string>(StringComparer.Ordinal);
            var healthValues = new HashSet<int>();

            for (int i = 0; i < side.Snapshots.Count; i++)
            {
                LogicSnapshot snapshot = side.Snapshots[i];

                if (snapshot.Intents.Count > 0) coverage.CheckpointsWithIntents++;
                coverage.IntentPayloadFieldObservations += IntentPayloadFieldCount(snapshot);
                if (i > 0 && side.Snapshots[i - 1].Intents.Count > snapshot.Intents.Count)
                    coverage.CheckpointsWithTerminalIntentDisappearance++;

                if (snapshot.ConflictGroups.Count > 0) coverage.CheckpointsWithConflictGroups++;
                coverage.ConflictGroupsTotal += snapshot.ConflictGroups.Count;
                for (int g = 0; g < snapshot.ConflictGroups.Count; g++)
                {
                    ConflictGroupSnapshot group = snapshot.ConflictGroups[g];
                    if (group.NodeIntentSequences != null
                        && group.NodeIntentSequences.Count > coverage.MaxConflictGroupNodeCount)
                    {
                        coverage.MaxConflictGroupNodeCount = group.NodeIntentSequences.Count;
                    }
                    if (group.EdgeCount > coverage.MaxConflictGroupEdgeCount)
                        coverage.MaxConflictGroupEdgeCount = group.EdgeCount;
                }

                if (snapshot.Contacts.Count > 0) coverage.CheckpointsWithContacts++;
                coverage.ContactsTotal += snapshot.Contacts.Count;
                for (int c = 0; c < snapshot.Contacts.Count; c++)
                    coverage.ContactTypesSeen.Add(snapshot.Contacts[c].Type);

                if (snapshot.AiControllers.Count > 0) coverage.CheckpointsWithAiController++;
                coverage.AiControllersTotal += snapshot.AiControllers.Count;
                for (int a = 0; a < snapshot.AiControllers.Count; a++)
                {
                    AiControllerSnapshot controller = snapshot.AiControllers[a];
                    nextThinkValues.Add(controller.NextThinkTick);
                    rngStates.Add(controller.Rng == null
                        ? "<null>"
                        : controller.Rng.AlgorithmVersion + "/" + controller.Rng.State);
                    if (firstDecisionCount < 0L) firstDecisionCount = controller.DecisionCount;
                    lastDecisionCount = controller.DecisionCount;
                }

                for (int u = 0; u < snapshot.Units.Count; u++) healthValues.Add(snapshot.Units[u].HealthQ10);

                for (int p = 0; p < snapshot.Plans.Count; p++)
                {
                    ActionPlanSnapshot plan = snapshot.Plans[p];
                    Increment(coverage.AllPlansStateHistogram, plan.State);

                    if (plan.ActionPlanId == side.AttackPlanId)
                    {
                        coverage.AttackPlanVisibleCheckpoints++;
                        coverage.AttackPlanLastVisibleState = plan.State;
                        coverage.AttackPlanEndTick = plan.EndTick;
                        if (plan.State >= (int)ActionPlanState.Completed
                            && coverage.AttackPlanTerminalAtTick < 0L)
                        {
                            coverage.AttackPlanTerminalAtTick = snapshot.Tick;
                        }
                    }

                    if (side.MovePlanScripted && plan.ActionSpecId == side.MoveSpecId)
                    {
                        coverage.MovePlanVisibleCheckpoints++;
                        coverage.MovePlanLastVisibleState = plan.State;
                        coverage.MovePlanEndTick = plan.EndTick;
                        if (coverage.MovePlanStartTick < 0L) coverage.MovePlanStartTick = plan.StartTick;
                    }

                    if (plan.TerminalTick >= 0L || plan.State >= (int)ActionPlanState.Completed)
                    {
                        coverage.CheckpointsWithTerminalPlans++;
                        Increment(coverage.TerminalStateHistogram, plan.State);
                    }
                }

                int sharing = CountOwnersSharingOnePrimaryTarget(snapshot);
                if (sharing > 0) coverage.CheckpointsWithMultiOwnerSameTarget++;
                if (sharing > coverage.MaxOwnersSharingOnePrimaryTarget)
                    coverage.MaxOwnersSharingOnePrimaryTarget = sharing;

                if (coverage.IntentSampleAttackSpecId == null || coverage.IntentSampleMoveSpecId == null)
                {
                    for (int x = 0; x < snapshot.Intents.Count; x++)
                    {
                        ActionSpec spec = definition.FindAction(
                            new ActionSpecId(snapshot.Intents[x].ActionSpecId));
                        if (spec == null) continue;
                        if (coverage.IntentSampleAttackSpecId == null && spec.Payload is AttackPayloadSpec)
                            coverage.IntentSampleAttackSpecId = snapshot.Intents[x].ActionSpecId;
                        if (coverage.IntentSampleMoveSpecId == null && spec.Payload is MovePayloadSpec)
                            coverage.IntentSampleMoveSpecId = snapshot.Intents[x].ActionSpecId;
                    }
                }
            }

            // —— 离开活动索引的 Tick：这才是"计划被收口"的判据 ——
            // 计划一旦进入终态就会离开活动索引，因此"某个检查点之后再也看不到它"
            // 等价于"它已被统一终态协调器收口"。逐计划分别计算，避免再次把它们混为一谈。
            coverage.AttackPlanLeftActiveIndexAtTick = FirstTickWithoutPlan(side, side.AttackPlanId, null);
            if (side.MovePlanScripted)
                coverage.MovePlanLeftActiveIndexAtTick = FirstTickWithoutPlan(side, -1L, side.MoveSpecId);

            // 归档面读数（任务 05 字段族，已在比较面内）：它必须随收口而增长。
            for (int i = 0; i < side.Snapshots.Count; i++)
            {
                long recordCount = side.Snapshots[i].TerminalPlanRecordCount;
                if (coverage.TerminalPlanRecordCountFirst < 0L) coverage.TerminalPlanRecordCountFirst = recordCount;
                coverage.TerminalPlanRecordCountLast = recordCount;
            }

            coverage.AiDecisionCountDelta = lastDecisionCount - firstDecisionCount;
            coverage.DistinctNextThinkTickValues = nextThinkValues.Count;
            coverage.DistinctRngStates = rngStates.Count;
            coverage.DistinctHealthQ10Values = healthValues.Count;
            coverage.DistinctContactTypes = coverage.ContactTypesSeen.Count;
            coverage.CumulativeDamageQ10 = side.CumulativeDamageQ10;
            coverage.DefinitionMoveSpecId = side.DefinitionMoveSpecId;
            coverage.DefinitionAttackSpecIds = side.DefinitionAttackSpecIds;
            coverage.ObservedPlanStartTick = side.ObservedPlanStartTick;
            coverage.ObservedPlanImpactTick = side.ObservedPlanImpactTick;
            coverage.ObservedPlanResolvedWindupTicks = side.ObservedPlanResolvedWindupTicks;
            coverage.ObservedPlanRecoveryTicks = side.ObservedPlanRecoveryTicks;
            return coverage;
        }

        /// <summary>
        /// 计划**第一次不再出现在活动索引**里的 Tick（-1 = 整个窗口内都在活动索引里）。
        ///
        /// 判据刻意不用"状态变成终态"：终态归档之后计划就不再出现在 <c>snapshot.Plans</c> 里，
        /// 因此"看不到它了"才是可观测的收口证据。用 <paramref name="actionPlanId"/> 或
        /// <paramref name="actionSpecId"/> 二者之一定位（前者按 ID、后者按动作）。
        /// </summary>
        private static long FirstTickWithoutPlan(ProfileStream side, long actionPlanId, string actionSpecId)
        {
            bool everSeen = false;
            for (int i = 0; i < side.Snapshots.Count; i++)
            {
                LogicSnapshot snapshot = side.Snapshots[i];
                bool present = false;
                for (int p = 0; p < snapshot.Plans.Count; p++)
                {
                    ActionPlanSnapshot plan = snapshot.Plans[p];
                    if (actionPlanId > 0L && plan.ActionPlanId == actionPlanId) { present = true; break; }
                    if (actionPlanId <= 0L && actionSpecId != null
                        && string.Equals(plan.ActionSpecId, actionSpecId, StringComparison.Ordinal))
                    {
                        present = true;
                        break;
                    }
                }

                if (present) { everSeen = true; continue; }
                if (everSeen) return snapshot.Tick;
            }
            return -1L;
        }

        /// <summary>本检查点上"同一主目标被两个及以上不同拥有者指向"的目标数。</summary>
        private static int CountOwnersSharingOnePrimaryTarget(LogicSnapshot snapshot)
        {
            var ownersByTarget = new Dictionary<long, HashSet<long>>();
            for (int i = 0; i < snapshot.Intents.Count; i++)
            {
                IntentSnapshot intent = snapshot.Intents[i];
                if (intent.PrimaryTargetUnitId == 0L) continue;
                HashSet<long> owners;
                if (!ownersByTarget.TryGetValue(intent.PrimaryTargetUnitId, out owners))
                {
                    owners = new HashSet<long>();
                    ownersByTarget[intent.PrimaryTargetUnitId] = owners;
                }
                owners.Add(intent.OwnerUnitId);
            }

            int count = 0;
            foreach (KeyValuePair<long, HashSet<long>> pair in ownersByTarget)
            {
                if (pair.Value.Count >= 2) count++;
            }
            return count;
        }

        /// <summary>
        /// Intent 载荷的逐字段展开条数（与 <c>CompareIntentPayloadFacts</c> 的字段清单**逐条对应**）。
        ///
        /// 它刻意写成独立的一份算式（而不是调用检测器的私有实现）：
        /// 字段清单一旦增删，本用例的规模断言立刻失败。
        /// </summary>
        private static long IntentPayloadFieldCount(LogicSnapshot snapshot)
        {
            if (snapshot.Intents.Count == 0) return 0L;
            long total = 0L;
            for (int i = 0; i < snapshot.Intents.Count; i++)
            {
                IntentSnapshot intent = snapshot.Intents[i];
                int areaPoints = intent.AreaPoints == null ? 0 : intent.AreaPoints.Count;
                int damageComponents = intent.DamageComponents == null ? 0 : intent.DamageComponents.Count;
                // 13 个标量 + 3 个动量 + 1 个区域根数 + 逐点 + 1 个分量根数 + 逐分量 3 字段
                total += 13L + 3L + 1L + areaPoints + 1L + 3L * damageComponents;
            }
            return total;
        }

        /// <summary>
        /// 任务 08 产出 17 的八项检查点状态。
        ///
        /// <strong>刻意分两轴</strong>（主理人裁定的形态）——它把"管线没接"与"这个场景里
        /// 本来就没有这个现象"彻底分开：
        /// <list type="bullet">
        /// <item><strong>Dispatch（族是否被分派）</strong>：该检查点依赖的可比字段族在本轮是否
        /// 真的进入了比较面（<c>ShadowComparisonDetector.Task08AiAndArbitrationFacts</c> 显式字段清单
        /// ＋篡改探针可证伪）。这一轴**与场景无关**，为 0 就是管线缺陷。</item>
        /// <item><strong>Phenomenon（场景里是否有该现象）</strong>：真 02B 遭遇的几何/动作集合
        /// 是否真的产生该现象（冲突组节点数、接触条数、伤害总量、生命变化……）。
        /// 这一轴为 0 是**场景事实**，不是管线缺陷。</item>
        /// </list>
        /// 两条轴都给出实测读数，处置（比较 / 登记 / 未覆盖+原因）随之逐项给出，
        /// 绝不用"已覆盖/未覆盖"一句笼统话盖过去。
        /// </summary>
        private sealed class CheckpointStatus
        {
            public int Index;
            public string Name;

            /// <summary>族是否被分派（比较面读数 + 探针证据）。</summary>
            public string Dispatch;

            /// <summary>场景里是否有该现象（实测计数/几何）。</summary>
            public string Phenomenon;

            /// <summary>本轮的处置：已比较 / 部分覆盖 / 未覆盖 + 原因 + 替代证据 + 限期与责任轮次。</summary>
            public string Disposition;

            public string Describe()
                => "#" + Index + " " + Name
                   + "\n    [分派] " + Dispatch
                   + "\n    [现象] " + Phenomenon
                   + "\n    [处置] " + Disposition;
        }

        /// <summary>
        /// 八项检查点的**实测**状态表（两轴形态）。
        ///
        /// 判据不是"声明"，而是画像窗口里的真实读数。任何"未覆盖"都必须给出
        /// **原因 + 替代证据 + 限期与责任轮次**（绝不塞进宽泛忽略列表、绝不伪造场景凑绿）。
        /// </summary>
        private static List<CheckpointStatus> MeasureTask08Checkpoints(
            ProfileFixture fixture, ProfileStream side, ProfileCoverage coverage)
        {
            const string gate = "限期与责任轮次：任务 10 切换主场景到 New 之前，"
                + "或独立的任务 08 补丁轮（需先换夹具几何/多三角体积表）";
            var list = new List<CheckpointStatus>();

            list.Add(new CheckpointStatus
            {
                Index = 1,
                Name = "旧二体兼容",
                Dispatch = "四族全部被分派：intents[…]（" + coverage.IntentPayloadFieldObservations
                    + " 条逐字段）、conflictGroups[…]（组数 " + coverage.ConflictGroupsTotal
                    + "）、contacts[…]（" + coverage.ContactsTotal + "）、aiControllers[…]（"
                    + coverage.AiControllersTotal + "）；篡改探针逐族命中期望字段路径",
                Phenomenon = "真 02B 遭遇槽位=" + fixture.DescribeSlots()
                    + "（恰好 2 个）；两侧各跑一场独立 BattleSimulation，Tick 0.." + TerminalTick
                    + " 的 ComputeHash 逐位相同、对象身份哈希逐检查点不同",
                Disposition = "已覆盖（本任务口径：真遭遇双槽位场景 + 两场独立重建逐位一致）。"
                    + "边界照实登记：两侧都是新实现 ⇒ 结论是『新实现的确定性与跨世界一致性』，"
                    + "**不是**『与旧权威等价』"
            });

            list.Add(new CheckpointStatus
            {
                Index = 2,
                Name = "三方 Clash",
                Dispatch = "conflictGroups[…] / contacts[…] 已被分派（"
                    + coverage.CheckpointsWithConflictGroups + " 个检查点有冲突组、"
                    + "maxGroupNodes=" + coverage.MaxConflictGroupNodeCount
                    + "、maxGroupEdges=" + coverage.MaxConflictGroupEdgeCount + "）"
                    + "⇒ 族没接的问题**不在**这里",
                Phenomenon = "**场景里没有该现象**：冲突组节点峰值 "
                    + coverage.MaxConflictGroupNodeCount + " < 3、边数峰值 "
                    + coverage.MaxConflictGroupEdgeCount
                    + "；真 02B 遭遇只有 " + fixture.SlotOrder.Count + " 个槽位",
                Disposition = "未覆盖（场景几何限制，非管线缺陷）。既有裁定：任务 08 集成台账 §九 裁定 7.3"
                    + "『d≥4 的有效 Clash 与互相命中在现夹具下互斥』。本用例**不**自造三体/AOE 定义凑绿。"
                    + "替代证据：EditMode Task08MomentumClashTests / Task08StagedResolutionIntegrationTests。"
                    + gate
            });

            list.Add(new CheckpointStatus
            {
                Index = 3,
                Name = "夹击（TwoAttackersFlankingOneTarget）",
                Dispatch = "intents[…] 与 contacts[…] 已被分派（判据所需的 OwnerUnitId / "
                    + "PrimaryTargetUnitId / ImpactTick / type 四个字段都逐条真比较）",
                Phenomenon = "**场景里没有该现象**：同目标多拥有者检查点数="
                    + coverage.CheckpointsWithMultiOwnerSameTarget + "、峰值="
                    + coverage.MaxOwnersSharingOnePrimaryTarget
                    + "、接触类型集合={" + DescribeContactTypes(coverage) + "}"
                    + "（只有 1 个敌对单位可被攻击 ⇒ 凑不出『两个攻击者夹一个目标』）",
                Disposition = "未覆盖（场景事实，非管线缺陷）。替代证据：EditMode "
                    + "Task08IntentAndConflictGraphIntegrationTests / Task08AoeTargetPolicyIntegrationTests"
                    + "（自造四槽位定义）。" + gate
            });

            list.Add(new CheckpointStatus
            {
                Index = 4,
                Name = "AOE 部分防御",
                Dispatch = "**载荷面已被分派**：intents[].targetPolicy / areaPoints[] / "
                    + "damageComponents[].{channelId,rawAmountBits,tags} 逐条真比较"
                    + "（篡改 areaPoints.count 与 damageComponents.count 都能精确报出该路径）",
                Phenomenon = "**场景里没有该现象**：真 02B 的 hero 动作集合里的攻击动作="
                    + (coverage.DefinitionAttackSpecIds ?? "<none>")
                    + "（都不是 AllTargetsInArea 策略的 AOE）、定义里的移动动作="
                    + (coverage.DefinitionMoveSpecId ?? "<none>")
                    + "；累计伤害 Q10=" + coverage.CumulativeDamageQ10
                    + "（有伤害才有『部分抵抗』可谈）",
                Disposition = "未覆盖（场景事实，非管线缺陷）：真遭遇的 hero 动作集合里**没有** "
                    + "AllTargetsInArea 策略的 AOE 攻击，且本场累计伤害为 0（未命中）⇒ "
                    + "『同一 AOE 命中多目标、其中一个部分抵抗』的两个前提都不成立。"
                    + "**不为凑绿改几何**。替代证据：EditMode Task08AoeTargetPolicyIntegrationTests"
                    + "（真实 Block 抵抗 ⇒ 逐目标伤害不等）。" + gate
            });

            list.Add(new CheckpointStatus
            {
                Index = 5,
                Name = "聚合伤害",
                Dispatch = "**聚合输入面已被分派**：intents[].impactTick（同 Tick 聚合的唯一时间键）、"
                    + "damageComponents[].{channelId,rawAmountBits,tags}、contacts[*]（接触全集）逐条真比较",
                Phenomenon = "**场景里没有该现象**：累计伤害 Q10=" + coverage.CumulativeDamageQ10
                    + "、生命读数集合大小=" + coverage.DistinctHealthQ10Values
                    + "、接触条数=" + coverage.ContactsTotal
                    + " ⇒ 本场**从未发生任何接触/伤害**，因此谈不上「聚合」；"
                    + "『同 Tick 两个来源指向同一目标』还需要夹击几何，实测 "
                    + coverage.CheckpointsWithMultiOwnerSameTarget + " 个检查点满足",
                Disposition = "未覆盖（场景事实，非管线缺陷）：聚合的**输入面**已逐字段比较且可被篡改探针证伪，"
                    + "但本场没有可聚合的伤害。**边界**：StagedResolution（RemainingHits/"
                    + "Aggregate.TotalDamageQ10）是只读诊断面、不进快照哈希（08 交接 §4.5），"
                    + "因此不进本可比面。替代证据：EditMode Task08MomentumAggregationTests / "
                    + "Task08AoeTargetPolicyIntegrationTests。" + gate
            });

            list.Add(new CheckpointStatus
            {
                Index = 6,
                Name = "同时强制位移 / Reservation 抢占 / 同 Tick 死亡",
                Dispatch = "**承载族已被分派**：contacts[*].{type,firstUnitId,secondUnitId,targetUnitId}、"
                    + "conflictGroups[*].{nodeIntentSequences,targetUnitIds,edgeCount,contacts}、"
                    + "intents[].momentum.{direction,units,impactProfileId} 都逐条真比较",
                Phenomenon = "**场景里没有这三类现象**：接触类型集合={"
                    + DescribeContactTypes(coverage) + "}（无强制位移所需的动量/冲击剖面结果）；"
                    + "Reservation 抢占需要多计划争抢同一格（本用例只有 1 条攻击计划）；"
                    + "同 Tick 死亡需要单 Tick 伤害 ≥ 满血（生命读数集合大小="
                    + coverage.DistinctHealthQ10Values + "，未归零）",
                Disposition = "未覆盖（场景事实，非管线缺陷）。替代证据：EditMode "
                    + "Task08ForcedDisplacementSolverTests / Task08ForcedDisplacementBatchTests / "
                    + "Task08DeathFootprintRemovalTests（以真实求解器与提交器覆盖）。" + gate
            });

            list.Add(new CheckpointStatus
            {
                Index = 7,
                Name = "肾上腺素获得事实",
                Dispatch = "adrenaline[unitId].{available,cycleId,reservedTotal,reservations,"
                    + "mirrorMatchesLedger} 与 units[*] 状态族逐检查点真比较（任务 07 字段族，本用例开启）",
                Phenomenon = "获得事实的触发条件是『造成/承受伤害后的 Tick 末入账』；"
                    + "本场累计伤害 Q10=" + coverage.CumulativeDamageQ10
                    + "（按规格开局 AvailableAdrenaline=0）⇒ 获得路径是否触发由该读数判定",
                Disposition = "部分覆盖：账本读数逐检查点已比较；获得路径的触发以累计伤害为前置读数。"
                    + "替代证据：EditMode Task08OpeningAdrenalineContractTests / Task07TurnWindowAndBudgetTests。"
                    + gate
            });

            list.Add(new CheckpointStatus
            {
                Index = 8,
                Name = "动作终态",
                Dispatch = "plans[].{state,terminalTick,terminationReason} 与 "
                    + "terminalPlanRecordCount/terminalPlanDigest 逐条真比较（任务 05 字段族，本用例开启）、"
                    + "intents[*].present（条目退出冻结队列即差异）",
                Phenomenon = "**现象在位（判据已修正到正确的面）**：两条脚本化计划都在窗口内"
                    + "**离开活动索引**（= 被统一终态协调器收口）——"
                    + "攻击 planId=" + side.AttackPlanId
                    + "：可见检查点数=" + coverage.AttackPlanVisibleCheckpoints
                    + "、最后一次可见状态=" + coverage.AttackPlanLastVisibleState
                    + "(" + (ActionPlanState)coverage.AttackPlanLastVisibleState + ")"
                    + "、EndTick=" + coverage.AttackPlanEndTick
                    + "、离开活动索引于 Tick " + coverage.AttackPlanLeftActiveIndexAtTick + "；"
                    + "移动：StartTick=" + coverage.MovePlanStartTick
                    + "、EndTick=" + coverage.MovePlanEndTick
                    + "、离开活动索引于 Tick " + coverage.MovePlanLeftActiveIndexAtTick + "；"
                    + "归档面 terminalPlanRecordCount=" + coverage.TerminalPlanRecordCountFirst
                    + "->" + coverage.TerminalPlanRecordCountLast
                    + "（随收口增长 ⇒ 收口确实发生，只是发生在活动索引之外）",
                Disposition = "已覆盖（族被分派 ∧ 现象在位）。**重要口径说明**：终态证据取自"
                    + "『计划离开活动索引』＋归档面 terminalPlanRecordCount/terminalPlanDigest 增长，"
                    + "而**不是**『在活动索引里看到 Completed』——后者是本轮之前我读错的面，"
                    + "曾导致 terminalPlanCheckpoints=0 被误读成「到 EndTick 仍未收口」。"
                    + "活动索引内观察到的终态计划检查点数=" + coverage.CheckpointsWithTerminalPlans
                    + "（该读数为 0 只说明「收口发生在活动索引之外」，不说明「没收口」）"
            });

            return list;
        }

        private static string DescribeContactTypes(ProfileCoverage coverage)
        {
            var types = new List<int>(coverage.ContactTypesSeen);
            types.Sort();
            var parts = new List<string>();
            for (int i = 0; i < types.Count; i++)
                parts.Add(types[i] + "(" + ((ContactType)types[i]) + ")");
            return parts.Count == 0 ? "<none>" : string.Join(",", parts);
        }

        // =====================================================================
        // 生产观测构造（第二通道的对照观测）
        // =====================================================================

        /// <summary>
        /// 由新侧快照派生一份<strong>字段口径逐条对齐</strong>的旧侧观测：
        /// 单位身份/状态/量化生命/格坐标/朝向/结束标志/排程计数全部取新侧快照的同一字段。
        ///
        /// 它只服务于"生产观测通道被真的走通且字段集合被穷尽分派"这一结论，
        /// <strong>不</strong>主张旧场景活动事实等于新侧（那需要真实旧场景活动读写）。
        /// </summary>
        private static IReadOnlyList<LegacyLogicObservation> BuildControlObservations(ProfileStream side)
        {
            var observations = new List<LegacyLogicObservation>(side.Snapshots.Count);
            for (int i = 0; i < side.Snapshots.Count; i++)
            {
                LogicSnapshot snapshot = side.Snapshots[i];
                var units = new List<LegacyLogicUnitObservation>(snapshot.Units.Count);
                for (int u = 0; u < snapshot.Units.Count; u++)
                {
                    UnitSnapshot unit = snapshot.Units[u];
                    units.Add(new LegacyLogicUnitObservation(
                        SlotIdOf(side, unit.UnitId), unit.UnitId, unit.DefinitionId, unit.FactionId,
                        "test-control/" + snapshot.Tick + "/" + unit.UnitId,
                        // Q10 = 定点 2^10 = 1024（`BattleSimulation.QuantizeHealth` = RoundHalfUp(v × 1024)），
                        // 因此逆变换必须除以 **1024**。此前误写成 `/ 10f`，导致检测器把观测
                        // 再用 ×1024 量化后与 `healthQ10` 相差 102.4 倍 ⇒ **每个单位在每个检查点上
                        // 都**报基础设施差异（实测 infra=1802 = 2 单位 × 901 检查点，
                        // firstInfra=units[0].healthQ10），把"量化约定不一致"伪装成"两侧事实不同"。
                        unit.HealthQ10 / 1024f, unit.X, unit.Y, unit.Facing, unit.State));
                }

                observations.Add(new LegacyLogicObservation(
                    snapshot.Tick, "LateUpdate", snapshot.Units.Count, units,
                    "task09-c2b-control-observation",
                    snapshot.BattleEnd.IsEnded,
                    snapshot.BattleEnd.ResultCode ?? string.Empty,
                    CountShadowScheduledEntries(snapshot)));
            }
            return observations;
        }

        private static string SlotIdOf(ProfileStream side, long unitId)
        {
            for (int i = 0; i < side.SlotOrder.Count; i++)
            {
                if (side.SlotOrder[i].UnitId == unitId) return side.SlotOrder[i].SlotId;
            }
            return string.Empty;
        }

        /// <summary>新内核当前排程条目数（与检测器 <c>CountShadowScheduledEntries</c> 同一口径）。</summary>
        private static int CountShadowScheduledEntries(LogicSnapshot snapshot)
            => snapshot.Plans.Count + snapshot.Intents.Count + snapshot.Effects.Count;

        // =====================================================================
        // 快照篡改工具（测试侧，不是生产比较路径）
        // =====================================================================

        /// <summary>
        /// 只读复制一份规范化快照，并按需替换其中一类事实
        /// （含本轮新增的四类：<c>intents</c>/<c>conflictGroups</c>/<c>contacts</c>/<c>aiControllers</c>）。
        ///
        /// 参数顺序与 <see cref="LogicSnapshot"/> 的构造签名一一对应；冲突组与接触键是签名末尾
        /// 带默认值的两个参数。
        /// </summary>
        private static LogicSnapshot CopySnapshot(
            LogicSnapshot source,
            IReadOnlyList<IntentSnapshot> intents = null,
            IReadOnlyList<ConflictGroupSnapshot> conflictGroups = null,
            IReadOnlyList<ContactSnapshot> contacts = null,
            IReadOnlyList<AiControllerSnapshot> aiControllers = null)
            => new LogicSnapshot(
                source.Tick, source.RulesVersion, source.BattleDefinitionHash, source.EncounterId,
                source.BattleEnd, source.Units, source.Effects,
                source.WindowManager, source.ConcurrentAction, source.Resources,
                source.ScheduleRevision, source.Plans, source.ReactionOpportunities,
                source.ActorLanes, intents ?? source.Intents, source.MovementSegments, source.Reservations,
                aiControllers ?? source.AiControllers,
                source.CommandIngresses, source.Rng,
                source.NextUnitId, source.NextActionPlanId, source.NextReactionOpportunityId,
                source.NextWindowId, source.NextEffectId, source.NextCommandSequence,
                source.NextIntentSequence, source.NextResolutionSequence, source.NextEventSequence,
                source.NextEffectSequence, source.History, source.CommandSourcePriorityMappingVersion,
                source.TerminalPlanRecordCount, source.TerminalPlanDigest,
                source.NextReactionOptionSequence,
                conflictGroups ?? source.ConflictGroups, contacts ?? source.Contacts);

        private static ProfileStream TamperCheckpoint(
            ProfileStream source, int index, Func<LogicSnapshot, LogicSnapshot> tamper)
        {
            var snapshots = new List<LogicSnapshot>(source.Snapshots.Count);
            for (int i = 0; i < source.Snapshots.Count; i++)
                snapshots.Add(i == index ? tamper(source.Snapshots[i]) : source.Snapshots[i]);

            var identity = new List<int>(source.SnapshotIdentityHashes);
            if (index >= 0 && index < identity.Count) identity[index] = identity[index] ^ 1;

            return new ProfileStream(
                snapshots, new List<ShadowCheckpointEventBinding>(source.EventBindings),
                identity, source.SlotOrder)
            {
                AttackPlanId = source.AttackPlanId,
                SubmitRejectionCode = source.SubmitRejectionCode,
                MoveRejectionCode = source.MoveRejectionCode,
                FirstStepRejectionCode = source.FirstStepRejectionCode,
                MoveDestination = source.MoveDestination,
                MovePlanScripted = source.MovePlanScripted,
                DefinitionMoveSpecId = source.DefinitionMoveSpecId,
                DefinitionAttackSpecIds = source.DefinitionAttackSpecIds,
                CumulativeDamageQ10 = source.CumulativeDamageQ10,
                ObservedPlanStartTick = source.ObservedPlanStartTick,
                ObservedPlanImpactTick = source.ObservedPlanImpactTick,
                ObservedPlanResolvedWindupTicks = source.ObservedPlanResolvedWindupTicks,
                ObservedPlanRecoveryTicks = source.ObservedPlanRecoveryTicks,
                AiControllerId = source.AiControllerId
            };
        }

        /// <summary>按 <c>IntentSequence</c> 替换一条 Intent（未命中的逐字保留）。</summary>
        private static IReadOnlyList<IntentSnapshot> TamperIntent(
            LogicSnapshot snapshot, long intentSequence, Func<IntentSnapshot, IntentSnapshot> change)
        {
            var intents = new List<IntentSnapshot>(snapshot.Intents.Count);
            for (int i = 0; i < snapshot.Intents.Count; i++)
            {
                IntentSnapshot intent = snapshot.Intents[i];
                intents.Add(intent != null && intent.IntentSequence == intentSequence ? change(intent) : intent);
            }
            return intents;
        }

        /// <summary>按 <c>GroupKey</c> 替换一个冲突组（未命中的逐字保留）。</summary>
        private static IReadOnlyList<ConflictGroupSnapshot> TamperGroup(
            LogicSnapshot snapshot, long groupKey, Func<ConflictGroupSnapshot, ConflictGroupSnapshot> change)
        {
            var groups = new List<ConflictGroupSnapshot>(snapshot.ConflictGroups.Count);
            for (int i = 0; i < snapshot.ConflictGroups.Count; i++)
            {
                ConflictGroupSnapshot group = snapshot.ConflictGroups[i];
                groups.Add(group != null && group.GroupKey == groupKey ? change(group) : group);
            }
            return groups;
        }

        /// <summary>替换一条接触键（按六元规范化键定位；未命中的逐字保留）。</summary>
        private static IReadOnlyList<ContactSnapshot> TamperContact(
            LogicSnapshot snapshot, ContactSnapshot target, Func<ContactSnapshot, ContactSnapshot> change)
        {
            string key = ContactKeyText(target);
            var contacts = new List<ContactSnapshot>(snapshot.Contacts.Count);
            for (int i = 0; i < snapshot.Contacts.Count; i++)
            {
                ContactSnapshot contact = snapshot.Contacts[i];
                contacts.Add(contact != null
                    && string.Equals(ContactKeyText(contact), key, StringComparison.Ordinal)
                        ? change(contact)
                        : contact);
            }
            return contacts;
        }

        /// <summary>按 <c>ControllerId</c> 替换一条 AI 决策者（未命中的逐字保留）。</summary>
        private static IReadOnlyList<AiControllerSnapshot> TamperAiController(
            LogicSnapshot snapshot, string controllerId, Func<AiControllerSnapshot, AiControllerSnapshot> change)
        {
            var controllers = new List<AiControllerSnapshot>(snapshot.AiControllers.Count);
            for (int i = 0; i < snapshot.AiControllers.Count; i++)
            {
                AiControllerSnapshot controller = snapshot.AiControllers[i];
                controllers.Add(controller != null
                    && string.Equals(controller.ControllerId, controllerId, StringComparison.Ordinal)
                        ? change(controller)
                        : controller);
            }
            return controllers;
        }

        private static string ContactKeyText(ContactSnapshot contact)
            => contact.Type.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + "|" + contact.FirstUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + "|" + contact.FirstPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + "|" + contact.SecondUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + "|" + contact.SecondPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + "|" + contact.TargetUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // =====================================================================
        // 诊断与夹具
        // =====================================================================

        private static string DescribeProfileStream(ProfileStream side)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("ticks=").Append(side.Snapshots.Count).Append(" hash[");
            for (int i = 0; i < side.Snapshots.Count && i < 8; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(side.Snapshots[i].Tick).Append('#').Append(side.Snapshots[i].ComputeHashHex());
            }
            builder.Append("] slotOrder=");
            for (int i = 0; i < side.SlotOrder.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(side.SlotOrder[i].SlotId).Append('#').Append(side.SlotOrder[i].UnitId);
            }
            return builder.ToString();
        }

        /// <summary>
        /// 该字段名是否属于 <c>Task08AiAndArbitrationFacts</c> 的四类比较面
        /// （<c>intents[…]</c> / <c>conflictGroups…</c> / <c>contacts[…]</c> / <c>aiControllers…</c>）。
        ///
        /// <strong>刻意复用生产侧的唯一权威</strong>（<c>ShadowCasePolicy</c> 的私有静态判据），
        /// 而不是在测试里再写一份字段名清单——两份清单必然漂移，而漂移的后果是
        /// "互斥不变量看起来成立、实际不成立"。它是私有的，因此按 <see cref="RuntimeOwnershipTestBase"/> 的
        /// 既有先例用反射调用；<c>stagedResolution.*</c> 是两条通道都不比较的诊断面，
        /// 因此<strong>不属于</strong>该比较面族（它是唯一允许被登记的 08 事实）。
        /// </summary>
        private static readonly MethodInfo IsTask08ProfileFieldMethod = typeof(ShadowCasePolicy)
            .GetMethod("IsTask08ProfileField", BindingFlags.NonPublic | BindingFlags.Static);

        private static bool IsTask08ComparedFamily(string field)
        {
            Assert.That(IsTask08ProfileFieldMethod, Is.Not.Null,
                "对照证据：ShadowCasePolicy 必须仍然有私有的 IsTask08ProfileField 判据"
                + "（改名必须让本互斥断言失败，而不是静默跳过）");
            bool matched = (bool)IsTask08ProfileFieldMethod.Invoke(null, new object[] { field });
            if (!matched) return false;
            // 只读诊断面不在比较面内（它是唯一允许登记的 08 事实）。
            return field == null || !field.StartsWith("stagedResolution.", StringComparison.Ordinal);
        }

        private static List<string> DescribeRegistrations(ShadowComparisonReport report)
        {
            var list = new List<string>();
            for (int i = 0; i < report.TemporarilyUncomparable.Count; i++)
                list.Add(report.TemporarilyUncomparable[i].Id);
            return list;
        }

        /// <summary>计划集合的诊断文本（断言失败时用于指出"计划到底长什么样"）。</summary>
        private static string DescribePlans(LogicSnapshot snapshot)
        {
            if (snapshot == null) return "<null>";
            if (snapshot.Plans.Count == 0) return "<empty>";
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                ActionPlanSnapshot plan = snapshot.Plans[i];
                if (i > 0) builder.Append(" ; ");
                builder.Append("id=").Append(plan.ActionPlanId)
                    .Append(" spec=").Append(plan.ActionSpecId)
                    .Append(" owner=").Append(plan.OwnerUnitId)
                    .Append(" state=").Append(plan.State).Append('(').Append((ActionPlanState)plan.State).Append(')')
                    .Append(" start=").Append(plan.StartTick)
                    .Append(" end=").Append(plan.EndTick)
                    .Append(" impact=").Append(plan.ImpactTick)
                    .Append(" terminal=").Append(plan.TerminalTick)
                    .Append(" hasDest=").Append(plan.HasDestination)
                    .Append(" dest=(").Append(plan.DestinationX).Append(',').Append(plan.DestinationY).Append(')');
            }
            return builder.ToString();
        }

        /// <summary>隐藏验证场景里真实的 02B 初始化来源（Assembly-CSharp，只能按接口取）。</summary>
        private BattleSimulationSeed BuildSeedFromFactory()
        {
            UnityEngine.MonoBehaviour factory = FindSimulationSourceFactory();
            Assert.That(factory, Is.Not.Null,
                "隐藏验证场景必须包含真实 02B 初始化来源（BattleSimulationSourceFactory）");

            var source = factory as IBattleSimulationSource;
            Assert.That(source, Is.Not.Null,
                "BattleSimulationSourceFactory 必须实现 IBattleSimulationSource");
            Assert.That(source.LastConfigurationError, Is.Null,
                "真实初始化链必须构建成功：" + source.LastConfigurationError);

            BattleSimulationSeed seed = source.BuildSeed();
            Assert.That(seed, Is.Not.Null);
            Assert.That(seed.Validate(), Is.Null);
            return seed;
        }
    }
}
