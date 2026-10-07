using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    /// <summary>
    /// 任务 09「必须产出」16：<strong>Shadow 真实请求镜像</strong>。
    ///
    /// 被测面 = 一条生产链路：
    /// <list type="number">
    /// <item><see cref="ShadowAuthorityProtocol"/> —— 唯一模拟入口（<c>Step</c> 之前冻结批次之后
    /// 记录权威事实；<c>Step</c> 之后「登记 Envelope → 折叠事件 → 对未被拒者 <c>RecordAccepted</c>」）；</item>
    /// <item><see cref="ReplayAuthorityInput"/> —— 只含 <c>Player</c> 的权威事实与处置；</item>
    /// <item><see cref="ShadowPlayerRequestMirror"/> + <see cref="ShadowBattleRunner"/> ——
    /// 按<strong>原始提交 Tick</strong>把 Player 请求注入<strong>独立</strong> Logic 世界，每条恰好一次。</item>
    /// </list>
    ///
    /// 本用例只使用 <c>ProjectHero.Logic</c> 与 <c>ProjectHero.Compatibility.Runtime</c>
    /// （本程序集 asmdef 的边界）；定义与初始输入取自隐藏验证场景里真实的 02B 初始化来源，
    /// 不另造几何。
    /// </summary>
    public sealed class Task09ShadowMirrorTests : RuntimeOwnershipTestBase
    {
        /// <summary>Player 入口的 <c>ControllerId</c>（定义侧冻结值）。</summary>
        private static readonly ControllerId PlayerId = new ControllerId("controller.player");

        /// <summary>AI 入口的 <c>ControllerId</c>（它由新模拟自己重建，绝不镜像）。</summary>
        private static readonly ControllerId AiId = new ControllerId("controller.enemy_ai");

        private const long OpeningTick = 0L;
        private const long PlayerRequestTick = 1L;
        private const long AiRequestTick = 2L;
        private const long SystemRequestTick = 3L;

        [UnityTest]
        public IEnumerator ShadowMirrorsPlayerRequestsButRebuildsAiAndSystemExactlyOnce()
        {
            yield return LoadHiddenValidationScene();

            // 生产装配点证据：Bootstrap 的 Shadow runner 必须存在，且**真实请求镜像已经接上**
            // （空镜像会让"镜像面恒为空"这一缺陷静默通过，所以此处必须可失败）。
            //
            // 注意：Shadow runner 只在 Shadow 模式启动时才由 Bootstrap 建立
            // （`BattleRuntimeBootstrap.StartBattle` 的 Shadow 分支 → `EnsureShadowRunner`）。
            // 本用例不需要一场真的 Shadow 战斗（后面自己 `runner.Configure/Initialize` 驱动），
            // 因此这里经**生产方法**（反射，同文件底部的接缝说明）取得 runner，而不是伪造一个。
            if (!Bootstrap.BattleCreated)
            {
                Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                    "隐藏验证场景的 Legacy 战斗必须能启动：" + Bootstrap.StartupRejection);
            }

            ShadowBattleRunner runner = EnsureProductionShadowRunner();
            Assert.That(runner, Is.Not.Null,
                "Bootstrap 必须在 EnsureShadowRunner 里建立 Shadow runner（生产装配点）");
            Assert.That(runner.IsInitialized, Is.False,
                "本用例取得的是**未初始化**的 runner（Legacy 模式下还没开局），随后由本用例显式驱动");
            Assert.That(runner.MirrorIsAuthorityMirror, Is.True,
                "生产装配点必须把真实请求镜像接上（BattleRuntimeBootstrap.RebindShadowAuthority → "
                + "ShadowBattleRunner.AttachMirror）；恒为空镜像会让产出 16 的镜像面永远不注入任何 Player 事实");

            ProjectHero.Core.Compatibility.Runtime.BattleSimulationSeed seed = BuildSeedFromFactory();

            // ---------- 第一步：真实驱动一遍世界，记录权威输入 ----------
            ScriptedLiveRun live = RunScriptedLiveRecording(seed);

            Assert.That(live.Authority.AuthorityCommands.Count, Is.GreaterThan(0),
                "前提：真实驱动必须至少产生一条 Player 权威事实（否则本用例退化为恒真）");
            Assert.That(live.NonPlayerFactsInRecordedBatches, Is.EqualTo(2),
                "前提：同一次驱动里 AI 与 System 各有一条入口绑定事实");
            Assert.That(live.Authority.AuthorityCommands.Count, Is.EqualTo(live.PlayerRequests.Count),
                "权威事实条数必须等于该场 Player 请求条数（AI/System 不占额度、来源也不被改写）");

            // 处置面收口（产出 11 的口径：**成功或被拒**的命令都记录最终处置）：
            //   · 每条权威事实恰好一种处置 —— Accepted **或** ProcessorRejected；
            //   · 入口级拒绝（IngressRejected）不可能出现在事实侧：它从未获得 CommandSequence，
            //     也就从未进入任何冻结批次（所以不进 AuthorityCommands）。
            // 注意"被处理器拒绝"**不是**"没镜像"：镜像面镜像的是**入口接受**的可信请求
            // （Request 已进入冻结批次并拿到序号），处理器级处置是这次重演的**结果**，
            // 恰恰是本用例要逐条对齐的东西（产出 16 的比较面之一：命令接受/拒绝）。
            // 本次实测：`CloseOwnWindow(WindowId 1)` 在真实 seed 里没有对应窗口 ⇒
            // `add.parameter.window`（该稳定码在 `CommandCodes` 中定义为"窗口 ID 没有打开的窗口"）
            // 或同域拒绝码。
            int acceptedFacts = CountOutcomes(live.Authority, ReplayCommandOutcomeKind.Accepted);
            int processorRejectedFacts =
                CountOutcomes(live.Authority, ReplayCommandOutcomeKind.ProcessorRejected);
            int ingressRejectedFacts =
                CountOutcomes(live.Authority, ReplayCommandOutcomeKind.IngressRejected);
            string outcomeBreakdown = DescribeOutcomes(live.Authority);

            Assert.That(live.Authority.CommandOutcomes.Count,
                Is.EqualTo(acceptedFacts + processorRejectedFacts + ingressRejectedFacts),
                "处置计数自洽（三类之和必须等于处置总数）");
            Assert.That(ingressRejectedFacts, Is.EqualTo(0),
                "入口级拒绝不可能成为权威事实的处置：它从未获得 CommandSequence，也从未进入冻结批次");
            Assert.That(live.Authority.AuthorityCommands.Count,
                Is.EqualTo(acceptedFacts + processorRejectedFacts),
                "每条权威事实必须恰好一种处置（Accepted 或 ProcessorRejected）：事实="
                + live.Authority.AuthorityCommands.Count + " accepted=" + acceptedFacts
                + " processorRejected=" + processorRejectedFacts
                + " ingressRejected=" + ingressRejectedFacts
                + " —— 双记（同一序号既有 Accepted 又有 ProcessorRejected）会让本式不成立；"
                + "明细：" + outcomeBreakdown);

            // 与上面那条**互补**的口径（两条都要成立才算"恰好一种处置"）：
            //   · 存在性：每条事实都必须能找到自己的处置（否则是"漏记"）；
            //   · 唯一性：任何 (事实, 处置) 对不得重复（否则是"重复记"，即双记的另一形态）。
            AssertNoDispositionIsMissingOrDuplicated(live.Authority);
            Assert.That(acceptedFacts + processorRejectedFacts, Is.GreaterThan(0),
                "本次驱动必须至少留下一种处置（否则本用例退化为只统计 0）：" + outcomeBreakdown);

            // ① AI/System 不在 AuthorityCommands 里（来源种类 + 载荷身份 + 拒绝码三层）
            AssertAuthorityInputIsPlayerOnly(live.Authority, live.AiRequests, live.SystemRequests);

            // ② ExcludedNonAuthoritativeFactCount == 同批次的非 Player 事实数（逐批次重算），
            //    且必须 > 0：否则"排除了 AI/System"可能只是因为**根本没有** AI/System 请求进来（假绿）。
            int recomputedNonPlayer = 0;
            for (int i = 0; i < live.RecordedBatches.Count; i++)
            {
                FrozenCommandBatch batch = live.RecordedBatches[i];
                for (int j = 0; j < batch.Requests.Count; j++)
                    if (batch.Requests[j].SourceKind != CommandSourceKind.Player) recomputedNonPlayer++;
            }
            Assert.That(recomputedNonPlayer, Is.EqualTo(2));
            Assert.That(live.Authority.ExcludedNonAuthoritativeFactCount, Is.GreaterThan(0),
                "同一次驱动里必须真的发生过 AI/System 事实被排除（读数 > 0），否则本用例会假绿");
            Assert.That(live.Authority.ExcludedNonAuthoritativeFactCount, Is.EqualTo(recomputedNonPlayer),
                "排除计数必须等于同批次的非 Player 事实数（实现成“总是 0”即红）");

            // ---------- 第二步：镜像读数（逐条 + 恰好一次） ----------
            // 镜像的游标是**一次性**的（每个 Tick 只交出一次事实），因此**每个消费方各用一份实例**：
            //   · `mirrorForDiagnostics`：逐条读数 + “同 Tick 第二次为空” + 回退断言（都会被消费）；
            //   · `mirrorForControlWorld`：**专门**喂对照世界取证（必须在它自己未被消费时驱动）；
            //   · `mirrorForRunner`：喂 runner 走真实推进路径。
            // 反面教材（本轮实测踩到）：让同一实例既做诊断取数、又去驱动对照世界 ⇒ 游标已越过全部事实，
            // 对照世界一条都拿不到 ⇒ “镜像面交出 0 条”这种**假缺口**（`:165` 那条断言本身就是它已被消费的证明）。
            var mirrorForDiagnostics = new ShadowPlayerRequestMirror(live.Authority);
            var mirrorForControlWorld = new ShadowPlayerRequestMirror(live.Authority);
            var mirrorForRunner = new ShadowPlayerRequestMirror(live.Authority);

            int writesBefore = Bootstrap.ShadowWrites.Total;

            // 每条事实都在它自己的原始提交 Tick 上被交出，且逐条为 Player 入口 + 原样载荷。
            for (int i = 0; i < live.Authority.AuthorityCommands.Count; i++)
            {
                ReplayAuthorityCommand fact = live.Authority.AuthorityCommands[i];
                ShadowMirroredTickView view = mirrorForDiagnostics.RequestsForTick(fact.SubmittedAtTick);
                int foundAt = IndexOfFact(view.Commands, fact);
                Assert.That(foundAt, Is.GreaterThanOrEqualTo(0),
                    "原始提交 Tick " + fact.SubmittedAtTick + " 的读数里必须含这条权威事实");
                Assert.That(view.Requests[foundAt].ProducerControllerId.Value, Is.EqualTo(PlayerId.Value));
                Assert.That(view.Requests[foundAt].SubmittedAtTick, Is.EqualTo(fact.SubmittedAtTick),
                    "镜像必须携带原始提交 Tick 本身，而不是取注入时刻");
                Assert.That(view.Requests[foundAt].Request, Is.SameAs(fact.Request),
                    "镜像的是记录下来的请求对象本身，不做任何改写");
            }

            // 恰好一次：同一个 Tick 的第二次镜像查询不再交出新请求。
            Assert.That(mirrorForDiagnostics.RequestsForTick(live.FirstSubmittedTick).Requests, Is.Empty,
                "同一个 Tick 的第二次镜像查询必须为空：事实只交出一次");

            // 不变量：镜像必须把**每一条**权威事实都交出去过（缺口不能被伪装成"这一 Tick 本来没请求"）。
            // 分母取自对照世界：规范命令集合里实际出现的条数（下面第三步填值）。
            int controlWorldMirrored = 0;

            // 回退 Tick 是显式契约违规，不是“跳过一条”。
            // 单调性守卫由两个公开入口的**共同实现**（RequestsForTick）承担，因此这里对两个入口
            // 各断言一次——"回退必须显式失败"必须对**所有**入口成立，而不是只对委托方那条重载成立。
            long regressedTick = live.FirstSubmittedTick - 1L;
            Assert.Throws<LogicDefinitionException>(
                () => mirrorForDiagnostics.RequestsFor(regressedTick),
                "回退 Tick 必须显式失败（静默返回空会把镜像缺失伪装成“这一 Tick 本来没有请求”）："
                + "入口=RequestsFor，tick=" + regressedTick);
            Assert.Throws<LogicDefinitionException>(
                () => mirrorForDiagnostics.RequestsForTick(regressedTick),
                "回退 Tick 必须显式失败：入口=RequestsForTick（两个公开入口共用同一份单调性守卫），"
                + "tick=" + regressedTick);

            // ---------- 第三步：AI/System 由新模拟自己重建，绝不注入 ----------
            // 信封级证据：把镜像事实喂进一个对照世界，逐 Tick 检查规范命令集合 ——
            // 集合里必须恰好是 Player 那条，AI/System 的载荷身份一个都不许出现。
            // 用**未被消费过的新实例**驱动，否则游标已越过全部事实 ⇒ 对照世界拿到 0 条（假缺口）。
            controlWorldMirrored = AssertEnvelopeLevelMirrorIsPlayerOnly(seed, mirrorForControlWorld, live);

            Assert.That(controlWorldMirrored, Is.EqualTo(live.Authority.AuthorityCommands.Count),
                "镜像面交出的请求总数必须恰好等于权威事实数：多一条=二次注入，少一条=镜像缺失。"
                + live.Describe());
            Assert.That(controlWorldMirrored, Is.EqualTo(live.PlayerRequests.Count),
                "镜像不得注入非 Player 事实：AI/System 各 1 条绝不能被注入。" + live.Describe());
            Assert.That(mirrorForControlWorld.AllAuthorityCommandsMirrored, Is.True,
                "驱动对照世界的那份镜像必须走完全部权威事实（有缺口说明漏了某个 Tick）："
                + mirrorForControlWorld.Describe());
            Assert.That(mirrorForControlWorld.TotalMirroredRequests,
                Is.EqualTo(mirrorForControlWorld.AuthorityCommandCount),
                "驱动对照世界的那份镜像自报条数必须等于权威事实总数（缺口不能被伪装成“本来没请求”）。"
                + live.Describe() + " ; " + mirrorForControlWorld.Describe());
            Assert.That(mirrorForControlWorld.TotalMirroredRequests, Is.EqualTo(controlWorldMirrored),
                "镜像自报条数必须与规范命令集合里实际出现的条数一致（两侧不能各有一套计数）。"
                + live.Describe() + " ; " + mirrorForControlWorld.Describe());
            // 诊断实例的读数同样要自洽：它逐条取过每一事实，因此也必须"已交出全部"。
            Assert.That(mirrorForDiagnostics.TotalMirroredRequests,
                Is.EqualTo(mirrorForDiagnostics.AuthorityCommandCount),
                "诊断实例逐条取过每一事实 ⇒ 它也必须已交出全部权威事实："
                + mirrorForDiagnostics.Describe());

            // ---------- 第四步：runner 侧真实推进（独立 Logic 世界） ----------
            runner.Configure(seed, mirrorForRunner);
            runner.Initialize(new BattleRuntimeContext(
                BattleRuntimeMode.Shadow, Bootstrap.Ledger, Bootstrap.ShadowWrites, null));

            long lastRequestTick = live.LastSubmittedTick;
            runner.AdvanceFrame(new BattleFrameDelta(0.5f, false, false), lastRequestTick);

            Assert.That(runner.SimulationTick, Is.GreaterThanOrEqualTo(lastRequestTick),
                "独立世界必须真的被推进到最后一个提交 Tick");
            Assert.That(mirrorForRunner.RequestsFetched, Is.GreaterThan(0),
                "runner 的推进必须真的在取镜像（恒不取=根本没接上）");
            Assert.That(mirrorForRunner.AllAuthorityCommandsMirrored, Is.True,
                "runner 侧推进不得漏掉任何权威事实：" + mirrorForRunner.Describe());
            Assert.That(mirrorForRunner.TotalMirroredRequests,
                Is.EqualTo(live.Authority.AuthorityCommands.Count),
                "runner 侧镜像请求总数必须等于权威事实数：多一条=二次注入，少一条=镜像缺失");
            Assert.That(runner.MirroredRequestCount, Is.EqualTo(mirrorForRunner.TotalMirroredRequests),
                "runner 侧观察到的事实条数必须与镜像交出的条数一致（两侧不能各有一套计数）");
            Assert.That(runner.MirroredRequestCount, Is.EqualTo(live.PlayerRequests.Count),
                "镜像面只注入 Player 事实：AI/System 各 1 条绝不能被注入");

            // ---------- 第五步：Shadow 零写入（不变量 22） ----------
            ShadowWriteCounters writes = Bootstrap.ShadowWrites;
            Assert.That(writes.UnityObjectWrites, Is.EqualTo(0), "Shadow 不得写 Unity 对象：" + writes.Describe());
            Assert.That(writes.LegacyStateWrites, Is.EqualTo(0), "Shadow 不得写旧逻辑状态：" + writes.Describe());
            Assert.That(writes.FeedbackWrites, Is.EqualTo(0), "Shadow 不得播放反馈：" + writes.Describe());
            Assert.That(writes.ViewBindings, Is.EqualTo(0), "Shadow 不得绑定 View：" + writes.Describe());
            Assert.That(writes.RejectedAttempts, Is.EqualTo(0), "零写入路径不得有任何越界尝试：" + writes.Describe());
            Assert.That(writes.Total, Is.EqualTo(writesBefore),
                "本次镜像推进（含对照世界）不得让写入计数增加一条：" + writes.Describe());

            // runner 的只读检查点采样不推进时钟（比较只在只读检查点发生）。
            long tickBeforeCheckpoint = runner.SimulationTick;
            runner.CaptureCheckpoint(ShadowBattleRunner.DefaultCheckpointName);
            Assert.That(runner.SimulationTick, Is.EqualTo(tickBeforeCheckpoint),
                "只读检查点采样不得推进独立世界");

            // ---------- 第六步：生产推进路径的可观察事实（C2a 的边界，如实登记） ----------
            // 生产装配点已把镜像接上（上面已断言）。但生产推进路径目前**没有任何**把 Play
            // 命令投进新模拟入口的接缝：新模拟的 Player 事实只能来自"权威事实重演"。
            // 因此 Bootstrap 自己驱动一场 Shadow 战斗后，权威输入必须仍然为空、镜像必须一条都没交出。
            // 这是本轮交付边界的可观察事实（不是缺陷掩盖）：若哪天有人替新模拟补上了 Player 提交接缝，
            // 本条会先红，从而提示"镜像面开始有真实输入、必须重新核对等价性"。
            Assert.That(Bootstrap.StopBattle("task09-shadow-mirror-test"), Is.True,
                "第一场战斗必须能停止");
            Assert.That(Bootstrap.ReleaseBattle(), Is.True, "第一场战斗必须能释放");
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "第二场必须能以 Shadow 模式启动：" + Bootstrap.StartupRejection);

            ShadowBattleRunner productionRunner = Bootstrap.Shadow;
            Assert.That(productionRunner, Is.Not.Null, "Shadow 模式下必须存在 Shadow runner");
            for (int i = 0; i < 4; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }
            Assert.That(productionRunner.SimulationTick, Is.GreaterThan(0),
                "生产 Shadow 路径必须真的在推进独立世界");

            Assert.That(Bootstrap.ShadowAuthorityInput.AuthorityCommands.Count, Is.EqualTo(0),
                "本轮的边界事实：生产推进路径还没有把 Player 命令投进新模拟入口的接缝，"
                + "因此权威输入为空、镜像一条都不交出（空镜像 ⇒ 与 03B 基线逐位等价）");
            Assert.That(Bootstrap.ShadowAuthorityInput.ExcludedNonAuthoritativeFactCount, Is.EqualTo(0),
                "同理，生产路径上也没有 AI/System 入口事实被排除");
            Assert.That(productionRunner.MirroredRequestCount, Is.EqualTo(0),
                "镜像在生产推进路径上确实没有注入任何事实");

            Bootstrap.StopBattle("task09-shadow-mirror-test");
        }

        // =====================================================================
        // 断言辅助
        // =====================================================================

        /// <summary>按处置种类统计权威输入里的命令处置条数。</summary>
        private static int CountOutcomes(ReplayAuthorityInput authority, ReplayCommandOutcomeKind kind)
        {
            int count = 0;
            for (int i = 0; i < authority.CommandOutcomes.Count; i++)
                if (authority.CommandOutcomes[i].OutcomeKind == kind) count++;
            return count;
        }

        /// <summary>
        /// 处置明细（诊断用，直接进失败信息）：<c>种类|Issuer|ProducerOrdinal|CommandSequence|ReasonCode</c>。
        /// 没有它就无法在回报里给出"那条事实的真实拒绝码"。
        /// </summary>
        private static string DescribeOutcomes(ReplayAuthorityInput authority)
        {
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < authority.CommandOutcomes.Count; i++)
            {
                ReplayCommandOutcome outcome = authority.CommandOutcomes[i];
                if (i > 0) builder.Append(" ; ");
                builder.Append(outcome.OutcomeKind).Append('|')
                    .Append(outcome.Issuer.Value ?? "<null>").Append('|')
                    .Append(outcome.ProducerOrdinal).Append('|')
                    .Append(outcome.CommandSequence).Append('|')
                    .Append(outcome.ReasonCode ?? "<no-reason>");
            }
            return builder.Length > 0 ? builder.ToString() : "<no-outcomes>";
        }

        /// <summary>
        /// 既检验"漏记"也检验"重复记"（双记的另一种形态）：
        /// <list type="bullet">
        /// <item>每条权威事实（按 <c>ControllerId|ProducerOrdinal</c>）都必须存在至少一条处置；</item>
        /// <item>任何 <c>(事实, CommandSequence)</c> 对不得出现两次（同序号要么被接受、要么被拒绝，不能两者都记）。</item>
        /// </list>
        /// </summary>
        private static void AssertNoDispositionIsMissingOrDuplicated(ReplayAuthorityInput authority)
        {
            var seenPairs = new HashSet<string>(StringComparer.Ordinal);
            var factsWithDisposition = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < authority.CommandOutcomes.Count; i++)
            {
                ReplayCommandOutcome outcome = authority.CommandOutcomes[i];
                string factKey = (outcome.Issuer.Value ?? "<null>") + "|" + outcome.ProducerOrdinal;
                string pairKey = factKey + "#" + outcome.CommandSequence + "#" + outcome.OutcomeKind;
                Assert.That(seenPairs.Add(pairKey), Is.True,
                    "同一个 (事实, 处置) 不得记录两次：" + pairKey);
                factsWithDisposition.Add(factKey);
            }

            for (int i = 0; i < authority.AuthorityCommands.Count; i++)
            {
                ReplayAuthorityCommand command = authority.AuthorityCommands[i];
                Assert.That(factsWithDisposition.Contains(command.CanonicalKey), Is.True,
                    "每条权威事实都必须留下处置（漏记即双记的反面）：" + command.CanonicalKey
                    + " 明细：" + DescribeOutcomes(authority));
            }
        }

        /// <summary>① AI/System 不得出现在权威输入里（来源种类 + 载荷身份 + 拒绝码三层）。</summary>
        private static void AssertAuthorityInputIsPlayerOnly(
            ReplayAuthorityInput authority,
            IReadOnlyList<CommandRequest> aiRequests,
            IReadOnlyList<CommandRequest> systemRequests)
        {
            Assert.That(authority.AuthorityCommands.Count, Is.GreaterThan(0));

            for (int i = 0; i < authority.AuthorityCommands.Count; i++)
            {
                ReplayAuthorityCommand command = authority.AuthorityCommands[i];
                Assert.That(command.SourceKind, Is.EqualTo(CommandSourceKind.Player),
                    "权威回放输入的每一条都必须是 Player 来源（索引=" + i + "）");
                Assert.That(command.Issuer.Value, Is.EqualTo(PlayerId.Value),
                    "权威回放输入只来自 Player 入口（索引=" + i + "）");
                Assert.That(IndexOfRequest(aiRequests, command.Request), Is.EqualTo(-1),
                    "AI 请求不得出现在权威回放输入里");
                Assert.That(IndexOfRequest(systemRequests, command.Request), Is.EqualTo(-1),
                    "System 请求不得出现在权威回放输入里");
            }

            for (int i = 0; i < authority.CommandOutcomes.Count; i++)
            {
                ReplayCommandOutcome outcome = authority.CommandOutcomes[i];
                Assert.That(outcome.SourceKind, Is.EqualTo(CommandSourceKind.Player),
                    "命令处置事实只含 Player 来源（索引=" + i + "）");
                Assert.That(outcome.Issuer.Value, Is.EqualTo(PlayerId.Value),
                    "命令处置事实只来自 Player 入口（索引=" + i + "）");
                Assert.That(outcome.OutcomeKind, Is.Not.EqualTo(ReplayCommandOutcomeKind.Invalid),
                    "处置种类不得是 Invalid（保留值不得进入回放事件流，索引=" + i + "）");
                Assert.That(outcome.OutcomeKind == ReplayCommandOutcomeKind.Accepted
                        || outcome.OutcomeKind == ReplayCommandOutcomeKind.ProcessorRejected,
                    Is.True,
                    "每条 Player 事实的处置只能是 Accepted 或 ProcessorRejected（入口级拒绝不可能"
                    + "成为事实的处置）：索引=" + i + " 实际=" + outcome.OutcomeKind);
            }
        }

        /// <summary>
        /// 信封级证据：把镜像事实喂进一个对照世界，逐 Tick 断言规范命令集合里
        /// <strong>恰好</strong>是 Player 那一条，且 AI/System 载荷身份一个都不出现。
        /// </summary>
        /// <returns>对照世界里实际被注入的请求条数（"恰好一次"的独立分母）。</returns>
        private static int AssertEnvelopeLevelMirrorIsPlayerOnly(
            ProjectHero.Core.Compatibility.Runtime.BattleSimulationSeed seed,
            ShadowPlayerRequestMirror mirror,
            ScriptedLiveRun live)
        {
            int mirroredIntoControlWorld = 0;
            using (BattleSimulation control = BattleSimulation.Create(
                seed.Definition, seed.EncounterId, seed.RuntimeInputs))
            {
                IReadOnlyList<ShadowMirroredRequest> allMirrored = CollectAllMirrored(mirror, live);

                for (long tick = 0L; tick <= live.LastSubmittedTick; tick++)
                {
                    var mine = new List<ShadowMirroredRequest>();
                    for (int i = 0; i < allMirrored.Count; i++)
                        if (allMirrored[i].SubmittedAtTick == tick) mine.Add(allMirrored[i]);
                    mirroredIntoControlWorld += mine.Count;

                    for (int i = 0; i < mine.Count; i++)
                    {
                        CommandIngressEntry entry = control.CommandIngress.FindEntry(mine[i].ProducerControllerId);
                        Assert.That(entry, Is.Not.Null,
                            "镜像事实的 ProducerControllerId 必须能解析出入口：" + mine[i].Origin);
                        Assert.That(entry.Submit(mine[i].Request), Is.Null,
                            "前提：Player 事实在对照世界里也必须被入口接受：" + mine[i].Origin);
                    }

                    FrozenCommandBatch batch = control.CommandIngress.FreezeTick(tick);
                    StepResult result = control.Step(tick, batch);

                    FrozenTickCommandSet set = control.LastCommandSet;
                    Assert.That(set, Is.Not.Null);
                    Assert.That(set.Envelopes.Count, Is.EqualTo(mine.Count),
                        "Tick " + tick + " 的规范集合里只应有镜像进来的 Player 事实："
                        + "多一条=AI/System 被二次注入，少一条=镜像缺失");
                    for (int i = 0; i < set.Envelopes.Count; i++)
                    {
                        CommandEnvelope envelope = set.Envelopes[i];
                        Assert.That(envelope.SourceKind, Is.EqualTo(CommandSourceKind.Player),
                            "Tick " + tick + " 的 Envelope 只允许 Player 来源");
                        Assert.That(envelope.ControllerId.Value, Is.EqualTo(PlayerId.Value),
                            "Tick " + tick + " 的 Envelope 只允许来自 Player 入口");
                        Assert.That(ReferenceEquals(envelope.Request, mine[i].Request), Is.True,
                            "Tick " + tick + " 的 Envelope 必须是镜像进来的那个请求对象本身");
                    }

                    for (int i = 0; i < live.AiRequests.Count; i++)
                        AssertRequestNotInSet(set, live.AiRequests[i], "AI", tick);
                    for (int i = 0; i < live.SystemRequests.Count; i++)
                        AssertRequestNotInSet(set, live.SystemRequests[i], "System", tick);

                    _ = result;
                }
            }

            return mirroredIntoControlWorld;
        }

        private static void AssertRequestNotInSet(
            FrozenTickCommandSet set, CommandRequest request, string label, long tick)
        {
            for (int i = 0; i < set.Envelopes.Count; i++)
                Assert.That(ReferenceEquals(set.Envelopes[i].Request, request), Is.False,
                    label + " 请求不得出现在 Tick " + tick + " 的规范集合里（它由新模拟自己重建）");
            for (int i = 0; i < set.RejectedCommands.Count; i++)
            {
                CommandRejectionRecord rejection = set.RejectedCommands[i];
                if (rejection == null || rejection.Envelope == null) continue;
                Assert.That(ReferenceEquals(rejection.Envelope.Request, request), Is.False,
                    label + " 请求不得出现在 Tick " + tick + " 的拒绝集合里");
            }
        }

        /// <summary>按逻辑 Tick 升序把镜像事实收集成一份平铺清单（每个 Tick 只取一次）。</summary>
        private static IReadOnlyList<ShadowMirroredRequest> CollectAllMirrored(
            ShadowPlayerRequestMirror mirror, ScriptedLiveRun live)
        {
            var all = new List<ShadowMirroredRequest>();
            for (long tick = live.FirstSubmittedTick; tick <= live.LastSubmittedTick; tick++)
            {
                IReadOnlyList<ShadowMirroredRequest> requests = mirror.RequestsFor(tick);
                for (int i = 0; i < requests.Count; i++) all.Add(requests[i]);
            }
            return all;
        }

        private static int IndexOfFact(
            IReadOnlyList<ReplayAuthorityCommand> facts, ReplayAuthorityCommand fact)
        {
            for (int i = 0; i < facts.Count; i++)
                if (ReferenceEquals(facts[i], fact)) return i;
            return -1;
        }

        private static int IndexOfRequest(IReadOnlyList<CommandRequest> requests, CommandRequest request)
        {
            for (int i = 0; i < requests.Count; i++)
                if (ReferenceEquals(requests[i], request)) return i;
            return -1;
        }

        // =====================================================================
        // 真实驱动（旧侧）的记录器
        // =====================================================================

        /// <summary>一次真实驱动的结果：权威输入 + 逐批次事实 + 三类请求的载荷身份。</summary>
        private sealed class ScriptedLiveRun
        {
            public ReplayAuthorityInput Authority;
            public readonly List<FrozenCommandBatch> RecordedBatches = new List<FrozenCommandBatch>();
            public readonly List<CommandRequest> PlayerRequests = new List<CommandRequest>();
            public readonly List<CommandRequest> AiRequests = new List<CommandRequest>();
            public readonly List<CommandRequest> SystemRequests = new List<CommandRequest>();
            public int NonPlayerFactsInRecordedBatches;

            public long FirstSubmittedTick => Authority.AuthorityCommands[0].SubmittedAtTick;

            public long LastSubmittedTick =>
                Authority.AuthorityCommands[Authority.AuthorityCommands.Count - 1].SubmittedAtTick;

            /// <summary>
            /// 一次驱动的完整读数（诊断用，直接进失败信息）：事实条数 / 三类请求条数 /
            /// 非 Player 事实数 / 排除计数 / 每条事实的 <c>CanonicalKey@SubmittedAtTick</c>。
            ///
            /// 存在的理由：这些数字**只能**在一次真实 Unity 运行里得到。把它们放进断言消息，
            /// 可以让"镜像交出了 0 条"这类失败自带定位信息，而不是靠事后猜。
            /// </summary>
            public string Describe()
            {
                var builder = new System.Text.StringBuilder();
                builder.Append("facts=").Append(Authority.AuthorityCommands.Count)
                    .Append(" outcomes=").Append(Authority.CommandOutcomes.Count)
                    .Append(" playerReq=").Append(PlayerRequests.Count)
                    .Append(" aiReq=").Append(AiRequests.Count)
                    .Append(" systemReq=").Append(SystemRequests.Count)
                    .Append(" nonPlayerFacts=").Append(NonPlayerFactsInRecordedBatches)
                    .Append(" excluded=").Append(Authority.ExcludedNonAuthoritativeFactCount)
                    .Append(" ticks[").Append(FirstSubmittedTick).Append("..").Append(LastSubmittedTick)
                    .Append("] facts=[");
                for (int i = 0; i < Authority.AuthorityCommands.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(Authority.AuthorityCommands[i].CanonicalKey)
                        .Append('@')
                        .Append(Authority.AuthorityCommands[i].SubmittedAtTick);
                }
                builder.Append("] recordedBatches=").Append(RecordedBatches.Count);
                for (int i = 0; i < RecordedBatches.Count; i++)
                {
                    builder.Append(" {tick=").Append(RecordedBatches[i].TargetTick)
                        .Append(",requests=").Append(RecordedBatches[i].Requests.Count).Append('}');
                }
                return builder.ToString();
            }
        }

        /// <summary>
        /// 用真实定义/初始输入驱动世界，并在每次 <c>Step</c> 前后按
        /// <see cref="ShadowAuthorityProtocol"/> 的同一顺序收口权威输入。
        /// </summary>
        private static ScriptedLiveRun RunScriptedLiveRecording(
            ProjectHero.Core.Compatibility.Runtime.BattleSimulationSeed seed)
        {
            var run = new ScriptedLiveRun { Authority = new ReplayAuthorityInput() };

            using (BattleSimulation simulation = BattleSimulation.Create(
                seed.Definition, seed.EncounterId, seed.RuntimeInputs))
            {
                CommandIngressEntry player = simulation.CommandIngress.FindEntry(PlayerId);
                CommandIngressEntry ai = simulation.CommandIngress.FindEntry(AiId);
                CommandIngressEntry system = simulation.CommandIngress.FindEntry(
                    new ControllerId(BattleSimulation.SystemControllerId));
                Assert.That(player, Is.Not.Null, "真实定义必须注册 controller.player 入口");
                Assert.That(ai, Is.Not.Null,
                    "真实定义必须注册 controller.enemy_ai 入口（AI 不镜像，但必须有同批次事实）");
                Assert.That(system, Is.Not.Null, "模拟装配必须注册 controller.system 入口");

                // 驱动序列必须**先完成 Tick 0**：`BattleSimulation.Step` 的入参校验要求
                // `step.Tick == lastCompletedTick + 1`（否则 `STEP_TICK_NOT_CURRENT`），
                // 而新世界在首次 Step 之前 `Tick == -1` ⇒ 直接 `Step(1)` 是装配级契约违规。
                // 先走一个**空 Tick 0**，与生产路径（Bootstrap 把两侧推进到同一个旧逻辑 Tick，
                // 起点归一化在 runner 内完成）以及 EditMode 的 `Task09Fixture.StepNext(sim)`
                // 起步方式一致。
                {
                    FrozenCommandBatch opening = simulation.CommandIngress.FreezeTick(OpeningTick);
                    run.RecordedBatches.Add(opening);
                    run.Authority.RecordFrozenBatch(opening);
                    StepResult openingResult = simulation.Step(OpeningTick, opening);
                    ShadowAuthorityProtocol.RecordOutcomes(
                        run.Authority, simulation, OpeningTick, openingResult);
                }

                var ticks = new[] { PlayerRequestTick, AiRequestTick, SystemRequestTick };
                for (int i = 0; i < ticks.Length; i++)
                {
                    long tick = ticks[i];
                    CommandRequest request = CloseOwnWindow(tick);

                    CommandIngressEntry entry = i == 0 ? player : (i == 1 ? ai : system);
                    if (i == 0) run.PlayerRequests.Add(request);
                    else if (i == 1) run.AiRequests.Add(request);
                    else run.SystemRequests.Add(request);

                    CommandIngressRejection rejection = i == 2
                        ? SubmitSystemInternal(entry, request)
                        : entry.Submit(request);
                    Assert.That(rejection, Is.Null, "前提：入口必须接受这条请求（tick=" + tick + "）");

                    FrozenCommandBatch batch = simulation.CommandIngress.FreezeTick(tick);
                    run.RecordedBatches.Add(batch);
                    run.Authority.RecordFrozenBatch(batch);

                    for (int j = 0; j < batch.Requests.Count; j++)
                        if (batch.Requests[j].SourceKind != CommandSourceKind.Player)
                            run.NonPlayerFactsInRecordedBatches++;

                    StepResult result = simulation.Step(tick, batch);
                    ShadowAuthorityProtocol.RecordOutcomes(run.Authority, simulation, tick, result);
                }
            }

            return run;
        }

        private static CommandRequest CloseOwnWindow(long targetTick)
            => new CommandRequest(
                targetTick,
                new WindowCommandScope(new WindowId(1L)),
                new WindowCommandPayload(WindowCommandKind.CloseOwnWindow));

        // =====================================================================
        // 反射接缝（生产入口是 internal / private；本程序集没有 InternalsVisibleTo）
        // =====================================================================

        private static readonly MethodInfo EnsureShadowRunnerMethod = typeof(BattleRuntimeBootstrap)
            .GetMethod("EnsureShadowRunner", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// 经<strong>生产方法</strong> <c>BattleRuntimeBootstrap.EnsureShadowRunner()</c> 取得 Shadow runner。
        ///
        /// 为什么用反射而不是 <c>Bootstrap.Shadow</c>：runner 只在 Shadow 模式启动时才被建立，
        /// 而本用例不需要一场真的 Shadow 战斗（它自己 `Configure/Initialize/AdvanceFrame` 驱动，
        /// 以免与 Legacy 权威世界互相干扰）。反射只用来**调用生产方法**，
        /// 因此"镜像是否接上"这条断言仍然检验的是生产装配点，而不是测试自己接的线。
        /// </summary>
        private ShadowBattleRunner EnsureProductionShadowRunner()
        {
            Assert.That(EnsureShadowRunnerMethod, Is.Not.Null,
                "对照证据：BattleRuntimeBootstrap 必须仍然有私有的 EnsureShadowRunner()"
                + "（改名必须让本断言失败，而不是让本用例静默拿不到 runner）");
            return (ShadowBattleRunner)EnsureShadowRunnerMethod.Invoke(Bootstrap, null);
        }

        private static readonly MethodInfo SubmitSystemInternalMethod = typeof(CommandIngressEntry)
            .GetMethod("SubmitSystemInternal", BindingFlags.NonPublic | BindingFlags.Instance);

        private static CommandIngressRejection SubmitSystemInternal(
            CommandIngressEntry systemEntry, CommandRequest request)
        {
            Assert.That(SubmitSystemInternalMethod, Is.Not.Null,
                "对照证据：CommandIngressEntry.SubmitSystemInternal 必须仍然是 Logic 内部入口"
                + "（改名必须让本断言失败，而不是静默换成公开提交路径）");
            return (CommandIngressRejection)SubmitSystemInternalMethod.Invoke(
                systemEntry, new object[] { request });
        }

        /// <summary>隐藏验证场景里真实的 02B 初始化来源（Assembly-CSharp，只能按接口取）。</summary>
        private ProjectHero.Core.Compatibility.Runtime.BattleSimulationSeed BuildSeedFromFactory()
        {
            UnityEngine.MonoBehaviour factory = FindSimulationSourceFactory();
            Assert.That(factory, Is.Not.Null,
                "隐藏验证场景必须包含真实 02B 初始化来源（BattleSimulationSourceFactory）");

            var source = factory as IBattleSimulationSource;
            Assert.That(source, Is.Not.Null,
                "BattleSimulationSourceFactory 必须实现 IBattleSimulationSource");
            Assert.That(source.LastConfigurationError, Is.Null,
                "真实初始化链必须构建成功：" + source.LastConfigurationError);

            ProjectHero.Core.Compatibility.Runtime.BattleSimulationSeed seed = source.BuildSeed();
            Assert.That(seed, Is.Not.Null);
            Assert.That(seed.Validate(), Is.Null);
            return seed;
        }
    }
}
