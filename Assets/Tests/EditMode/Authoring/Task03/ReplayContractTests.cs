using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Authoring.Tests.Task03
{
    /// <summary>
    /// 任务 03 必需测试：Tick 0 重演数据契约（独立 <c>ReplayFormatVersion</c>、
    /// 完整运行时输入的 Header、首个 Step 前 <c>InitialStateHash</c>，
    /// 以及"玩家请求按原始提交 Tick 记录、AI/System 由逻辑重建"的输入边界）。
    /// 任务 03 只用最小夹具证明契约，不实现播放器。
    /// </summary>
    public class ReplayContractTests
    {
        [Test]
        public void ReplayFormatVersionIsIndependentFromRulesVersion()
        {
            var sim = Task03.NewSim();
            ReplayHeader header = sim.BuildReplayHeader();

            Assert.That(header.ReplayFormatVersion, Is.EqualTo(ReplayFormat.Version));
            Assert.That(header.RulesVersion, Is.EqualTo(Task03.Definition.RulesVersion));
            Assert.That(header.RulesVersion, Is.Not.EqualTo(ReplayFormat.Version.ToString()));
            Assert.That(Task03.Definition.RulesVersion, Is.EqualTo("battle-def-v1"));

            // 两个版本互不派生：只改玩法规则版本不改变格式版本，反之亦然。
            Assert.That(ReplayFormat.Version, Is.EqualTo(1));
            var ruleChanged = header with { RulesVersion = "battle-def-v2" };
            Assert.That(ruleChanged.ReplayFormatVersion, Is.EqualTo(header.ReplayFormatVersion));

            // 格式版本是常量且被冻结协议描述显式记录。
            Assert.That(ReplayFormat.FrozenProtocolDescription(), Does.Contain("version=" + ReplayFormat.Version));
            Assert.That(ReplayFormat.FrozenProtocolDescription(), Does.Contain(ReplayFormat.HashAlgorithmId));

            // 回放头部校验：格式版本与规则版本各有独立失败码。
            Assert.That(ReplayHeaderValidation.Validate(header, Task03.Definition), Is.Null);
            Assert.That(ReplayHeaderValidation.Validate(header with { ReplayFormatVersion = 99 }, Task03.Definition),
                Is.EqualTo(ReplayCodes.REPLAY_FORMAT_VERSION_MISMATCH));
            Assert.That(ReplayHeaderValidation.Validate(header with { RulesVersion = "other-rules" }, Task03.Definition),
                Is.EqualTo(ReplayCodes.REPLAY_RULES_VERSION_MISMATCH));
            Assert.That(ReplayHeaderValidation.Validate(header with { BattleDefinitionHash = "0badc0de" }, Task03.Definition),
                Is.EqualTo(ReplayCodes.REPLAY_DEFINITION_HASH_MISMATCH));
            Assert.That(ReplayHeaderValidation.Validate(header with { TicksPerSecond = 30 }, Task03.Definition),
                Is.EqualTo(ReplayCodes.REPLAY_TICKS_PER_SECOND_MISMATCH));
            Assert.That(ReplayHeaderValidation.Validate(header with { EncounterId = new EncounterDefinitionId("encounter.nope") },
                Task03.Definition), Is.EqualTo(ReplayCodes.REPLAY_ENCOUNTER_NOT_FOUND));
        }

        [Test]
        public void TickZeroReplayHeaderCarriesRuntimeInputsAndInitialStateHash()
        {
            var original = Task03.NewSim();
            ReplayHeader header = original.BuildReplayHeader();

            // Header 携带完整运行时输入（RNG 种子 + 局外资源），不得从当前存档/单例重新读取。
            Assert.That(header.RuntimeInputs.InitialRngSeed, Is.EqualTo(Task03.Inputs.InitialRngSeed));
            Assert.That(header.RuntimeInputs.InitialMetaResource, Is.EqualTo(Task03.Inputs.InitialMetaResource));
            Assert.That(header.TicksPerSecond, Is.EqualTo(Task03.Definition.TicksPerSecond));
            Assert.That(header.EncounterId, Is.EqualTo(Task03.EncounterId));

            // 首个 Step 之前的哈希必须能被独立重建的模拟复现。
            var rebuilt = BattleSimulation.Create(Task03.Definition, header.EncounterId, header.RuntimeInputs);
            Assert.That(rebuilt.InitialStateHash, Is.EqualTo(header.InitialStateHash));
            Assert.That(ReplayHeaderValidation.ValidateInitialState(header, rebuilt.InitialStateHash), Is.Null);

            // 种子变化 → 初始状态哈希变化（运行时输入确实是回放输入的一部分）。
            var otherSeed = new ProjectHero.Logic.Initialization.BattleRuntimeInputs(
                Task03.Inputs.InitialRngSeed + 1UL, Task03.Inputs.InitialMetaResource);
            var otherSim = BattleSimulation.Create(Task03.Definition, header.EncounterId, otherSeed);
            Assert.That(otherSim.InitialStateHash, Is.Not.EqualTo(header.InitialStateHash));

            // 哈希不匹配必须稳定拒绝重演。
            Assert.That(ReplayHeaderValidation.ValidateInitialState(header, otherSim.InitialStateHash),
                Is.EqualTo(ReplayCodes.REPLAY_INITIAL_STATE_HASH_MISMATCH));
        }

        [Test]
        public void RecordedPlayerFactsExcludeAiAndSystemSources()
        {
            var observer = new RecordingDecisionObserver
            {
                SubmitTargetTick = 1L,
                SubmitControllerId = Task03.EnemyAiController,
                SubmitOnce = true
            };
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[] { observer }));

            Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(0L));
            StepResult first = Task03.StepNext(sim);

            IReadOnlyList<RecordedCommandRequest> facts = sim.CommandIngress.LastFrozenPlayerFacts;
            Assert.That(facts.Count, Is.EqualTo(1));
            Assert.That(facts[0].Issuer.Value, Is.EqualTo(Task03.PlayerController));
            Assert.That(facts[0].SourceKind, Is.EqualTo(CommandSourceKind.Player));
            Assert.That(facts[0].ProducerOrdinal, Is.EqualTo(1L));
            Assert.That(facts[0].Request.TargetTick, Is.EqualTo(0L), "按原始提交 Tick 记录");
            Assert.That(facts[0].IsAuthoritativeReplayInput, Is.True);

            // 同一 Tick 的 AI 请求由逻辑重建：不出现在权威玩家输入记录中。
            StepResult second = Task03.StepNext(sim);
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(1));
            Assert.That(sim.LastCommandSet.Envelopes[0].ControllerId.Value, Is.EqualTo(Task03.EnemyAiController));
            Assert.That(sim.CommandIngress.LastFrozenPlayerFacts.Count, Is.EqualTo(0),
                "AI/System 事实不属于权威回放输入");
            Assert.That(second.Snapshot.NextCommandSequence, Is.EqualTo(3L));

            // 记录 DTO 不是运行时授权：它只能经入口重新验证后注入。
            var recorded = new RecordedCommandRequest(
                new ControllerId(Task03.PlayerController), CommandSourceKind.Player, 1L, Task03.ScheduleAdd(0L));
            Assert.That(recorded.IsAuthoritativeReplayInput, Is.True);
            Assert.That((recorded with { SourceKind = CommandSourceKind.Ai }).IsAuthoritativeReplayInput, Is.False);

            var replaySim = Task03.NewSim();

            // 发行者与目标入口不匹配 → 立即失败（不得把 DTO 当成运行时授权）。
            var issuerMismatch = Assert.Throws<LogicDefinitionException>(
                () => Task03.AiEntry(replaySim).InjectRecordedFact(recorded));
            Assert.That(issuerMismatch.ErrorCode, Is.EqualTo(CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID));

            // 非 Player 入口不接受记录事实注入（首版权威输入只接受 Player 来源）。
            CommandIngressRejection nonPlayer = Task03.AiEntry(replaySim).InjectRecordedFact(1L, Task03.ScheduleAdd(0L));
            Assert.That(nonPlayer, Is.Not.Null);
            Assert.That(nonPlayer.ReasonCode, Is.EqualTo(CommandCodes.REPLAY_NON_PLAYER_SOURCE_NOT_AUTHORITATIVE));

            // 玩家入口接受与自身绑定一致的记录事实。
            Assert.That(Task03.PlayerEntry(replaySim).InjectRecordedFact(recorded), Is.Null);
        }

        [Test]
        public void TickZeroReplayFromRecordedPlayerFactsReproducesIdenticalHashes()
        {
            var assembly = new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[]
                {
                    new RecordingDecisionObserver
                    {
                        SubmitTargetTick = 2L,
                        SubmitControllerId = Task03.EnemyAiController,
                        SubmitOnce = true
                    }
                });

            // —— 原始运行：记录每个 Tick 冻结的玩家可信事实与逐 Tick 快照哈希 ——
            var original = Task03.NewSim(assembly);
            var recordedByTick = new Dictionary<long, IReadOnlyList<RecordedCommandRequest>>();
            var hashesByTick = new Dictionary<long, string>();

            for (long tick = 0; tick < 5; tick++)
            {
                if (tick == 1) Task03.PlayerEntry(original).Submit(Task03.ScheduleAdd(1L));
                if (tick == 3) Task03.PlayerEntry(original).Submit(Task03.Block(3L, 1L));
                StepResult result = Task03.StepNext(original);
                recordedByTick[tick] = original.CommandIngress.LastFrozenPlayerFacts.ToArray();
                hashesByTick[tick] = result.SnapshotHashHex;
            }

            ReplayHeader header = original.BuildReplayHeader();

            // —— 重演：从 Tick 0 新建模拟，先验证初始哈希，再按原始提交 Tick 注入玩家事实 ——
            var replay = BattleSimulation.Create(Task03.Definition, header.EncounterId, header.RuntimeInputs, assembly);
            Assert.That(ReplayHeaderValidation.ValidateInitialState(header, replay.InitialStateHash), Is.Null);

            for (long tick = 0; tick < 5; tick++)
            {
                foreach (RecordedCommandRequest fact in recordedByTick[tick])
                {
                    CommandIngressRejection rejection = Task03.PlayerEntry(replay).InjectRecordedFact(fact);
                    Assert.That(rejection, Is.Null, "已记录的可信玩家事实必须能重新注入并通过入口校验");
                }

                StepResult result = Task03.StepNext(replay);
                Assert.That(result.SnapshotHashHex, Is.EqualTo(hashesByTick[tick]),
                    $"Tick {tick} 的重演哈希必须与原始运行一致（AI 请求由逻辑重建，不再次注入）");
            }

            Assert.That(replay.CurrentSnapshot.ComputeHashHex(), Is.EqualTo(original.CurrentSnapshot.ComputeHashHex()));
            Assert.That(replay.Tick, Is.EqualTo(original.Tick));
            Assert.That(replay.CurrentSnapshot.NextCommandSequence,
                Is.EqualTo(original.CurrentSnapshot.NextCommandSequence),
                "入口水位与后续 CommandSequence 在重演中保持一致");
        }
    }
}
