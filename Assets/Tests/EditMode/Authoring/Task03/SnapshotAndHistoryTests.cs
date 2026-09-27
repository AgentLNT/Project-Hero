using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Authoring.Tests.Task03
{
    /// <summary>
    /// 任务 03 必需测试：不可变历史归档、增量摘要、访问计数与规范化快照历史语义
    /// （主方案 3.11.2）。
    /// </summary>
    public class SnapshotAndHistoryTests
    {
        [Test]
        public void IncrementalHistoryDigestMatchesIndependentFullRecomputation()
        {
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                archiveCandidateSources: new IHistoryArchiveCandidateSource[] { new FixtureArchiveSource() }));

            ulong incremental = 0UL;
            for (long tick = 0; tick < 5; tick++)
            {
                StepResult result = Task03.StepNext(sim);
                incremental = result.Snapshot.History.DigestValue;
                Assert.That(result.Snapshot.History.RecordCount, Is.EqualTo(tick + 1L));

                // 每个检查点都用独立参照实现（从 H0 顺序重算，忽略增量缓存字段）比较同一摘要协议。
                ulong recomputed = sim.RecomputeHistoryDigestForDiagnostics();
                Assert.That(recomputed, Is.EqualTo(incremental), $"Tick {tick} 的增量摘要必须等于完整重算");
            }

            // 直接对归档器做一次同协议重算。
            var archive = new HistoryArchive();
            archive.AppendFinalizedOrdered(0L, new[]
            {
                Task03.Candidate(0L, HistoryRecordKind.ActionPlanTerminal, "plan.2"),
                Task03.Candidate(0L, HistoryRecordKind.WindowLedger, "window.1")
            });
            ulong full = HistoryDigestProtocol.RecomputeFromSeed(archive.CopyAllForDiagnostics());
            Assert.That(archive.Summary.DigestValue, Is.EqualTo(full));
        }

        [Test]
        public void HistoryArchiveOrderIgnoresCandidateEnumerationOrder()
        {
            var forward = new HistoryArchive();
            forward.AppendFinalizedOrdered(3L, new[]
            {
                Task03.Candidate(3L, HistoryRecordKind.WindowLedger, "window.2", true, 2L),
                Task03.Candidate(1L, HistoryRecordKind.ActionPlanTerminal, "plan.9", true, 9L),
                Task03.Candidate(3L, HistoryRecordKind.ActionPlanTerminal, "plan.1", true, 1L),
                Task03.Candidate(3L, HistoryRecordKind.ActionPlanTerminal, "plan.0", true, 0L)
            });

            var reversed = new HistoryArchive();
            reversed.AppendFinalizedOrdered(3L, new[]
            {
                Task03.Candidate(3L, HistoryRecordKind.ActionPlanTerminal, "plan.0", true, 0L),
                Task03.Candidate(3L, HistoryRecordKind.ActionPlanTerminal, "plan.1", true, 1L),
                Task03.Candidate(1L, HistoryRecordKind.ActionPlanTerminal, "plan.9", true, 9L),
                Task03.Candidate(3L, HistoryRecordKind.WindowLedger, "window.2", true, 2L)
            });

            Assert.That(reversed.Summary.Digest, Is.EqualTo(forward.Summary.Digest),
                "归档顺序固定为 ArchivedAtTick -> RecordKind 整数值 -> StableKey");
            Assert.That(reversed.Summary.RecordCount, Is.EqualTo(4L));
            Assert.That(reversed.RecordCount, Is.EqualTo(forward.RecordCount));

            // 规范顺序可直接读取验证。
            var view = forward.BuildDiagnosticView(4);
            Assert.That(view.Records.Select(r => r.StableKey).ToArray(),
                Is.EqualTo(new[] { "plan.9", "plan.0", "plan.1", "window.2" }));
            Assert.That(view.Records.Select(r => r.ArchiveIndex).ToArray(), Is.EqualTo(new long[] { 1, 2, 3, 4 }));
        }

        [Test]
        public void HistoryArchivesOnlyAfterCleanupAndInvariantChecks()
        {
            // 1. 不变量检查失败时：Step 明确失败，且本 Tick 不归档。
            var failing = Task03.NewSim(new BattleSimulationAssembly(
                invariantChecks: new IStepInvariantCheck[] { new FailingInvariantCheck() },
                archiveCandidateSources: new IHistoryArchiveCandidateSource[] { new FixtureArchiveSource() }));

            var violation = Assert.Throws<LogicDefinitionException>(() => Task03.StepNext(failing));
            Assert.That(violation.ErrorCode, Is.EqualTo(SimulationCodes.STEP_INVARIANT_VIOLATION));
            Assert.That(failing.History.RecordCount, Is.EqualTo(0L), "检查失败必须先于归档");
            Assert.That(failing.HistoryAccessCounters.NewRecordsProcessed, Is.EqualTo(0L));

            // 2. 正常路径：只读检查时刻归档尚未发生，且归档阶段是本 Tick 最后一个阶段。
            var recording = new RecordingInvariantCheck();
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                invariantChecks: new IStepInvariantCheck[] { recording },
                archiveCandidateSources: new IHistoryArchiveCandidateSource[] { new FixtureArchiveSource() }));
            recording.Simulation = sim;

            Task03.StepNext(sim);
            Assert.That(recording.CheckedTicks, Is.EqualTo(new[] { 0L }));
            Assert.That(sim.History.RecordCount, Is.EqualTo(1L), "检查通过后本 Tick 的候选被归档");

            var trace = sim.LastStepPhaseTrace;
            Assert.That(trace[trace.Count - 1].Phase, Is.EqualTo(StepPhase.InvariantCheckArchiveAndOutput),
                "不变量检查与归档是本 Tick 的最后一个阶段");
            Assert.That(trace.Select(e => e.Phase).ToList().IndexOf(StepPhase.PostDisplacementDeath),
                Is.LessThan(trace.Count - 1), "归档严格晚于清理与死亡阶段");
        }

        [Test]
        public void ArchivedRecordsHaveNoMutableAliasesAndRejectDuplicateAppend()
        {
            var archive = new HistoryArchive();
            byte[] payload = Task03.Payload(11L, 22L);
            var candidate = new HistoryArchiveCandidate(
                0L, HistoryRecordKind.ActionPlanTerminal, "plan.7", payload, HistorySealProof.Sealed);

            archive.AppendFinalizedOrdered(0L, new[] { candidate });
            byte[] frozen = archive.BuildDiagnosticView(1).Records[0].PayloadCopy();

            // 改写原数组不得影响已冻结记录。
            payload[0] = 0xFF;
            payload[1] = 0xFF;
            Assert.That(archive.BuildDiagnosticView(1).Records[0].PayloadCopy(), Is.EqualTo(frozen));

            // 取回的拷贝被改写也不影响归档。
            byte[] copy = archive.BuildDiagnosticView(1).Records[0].PayloadCopy();
            copy[0] = 0x01;
            Assert.That(archive.BuildDiagnosticView(1).Records[0].PayloadCopy(), Is.EqualTo(frozen));

            // 重复归档被拒绝，且失败批次不留部分写入。
            var duplicate = Assert.Throws<LogicDefinitionException>(() => archive.AppendFinalizedOrdered(0L, new[]
            {
                Task03.Candidate(0L, HistoryRecordKind.ActionPlanTerminal, "plan.8", true, 1L),
                Task03.Candidate(0L, HistoryRecordKind.ActionPlanTerminal, "plan.7", true, 2L)
            }));
            Assert.That(duplicate.ErrorCode, Is.EqualTo(HistoryArchive.HISTORY_RECORD_DUPLICATE));
            Assert.That(archive.RecordCount, Is.EqualTo(1L), "重复键必须在写入任何记录之前失败");

            // 记录类型必须是合法版本化整数键。
            var invalidKind = Assert.Throws<LogicDefinitionException>(() => archive.AppendFinalizedOrdered(0L, new[]
            {
                Task03.Candidate(0L, HistoryRecordKind.Invalid, "plan.zz", true, 1L)
            }));
            Assert.That(invalidKind.ErrorCode, Is.EqualTo(HistoryArchive.HISTORY_RECORD_KIND_INVALID));
            Assert.That(archive.RecordCount, Is.EqualTo(1L));
        }

        [Test]
        public void MutableWindowLedgerCannotBeArchivedWhileReservationsRemain()
        {
            var source = new FixtureArchiveSource
            {
                SealedPerTick = 0,
                UnsealedPerTick = 1,
                Kind = HistoryRecordKind.WindowLedger
            };

            var sim = Task03.NewSim(new BattleSimulationAssembly(
                archiveCandidateSources: new IHistoryArchiveCandidateSource[] { source }));

            Task03.StepNext(sim);
            Assert.That(sim.History.RecordCount, Is.EqualTo(0L), "关闭但仍可能有退款/消费的账本不得提前归档");
            Assert.That(sim.HistoryAccessCounters.IneligibleCandidatesSkipped, Is.EqualTo(1L));

            // 预留全部释放后，同一账本才被冻结。
            source.UnsealedPerTick = 0;
            source.SealedPerTick = 1;
            StepResult result = Task03.StepNext(sim);
            Assert.That(result.Snapshot.History.RecordCount, Is.EqualTo(1L));
            Assert.That(sim.HistoryAccessCounters.IneligibleCandidatesSkipped, Is.EqualTo(1L));
        }

        [Test]
        public void EmptyArchiveBatchDoesNotReadCopyOrHashOldHistory()
        {
            var source = new FixtureArchiveSource { SealedPerTick = 1 };
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                archiveCandidateSources: new IHistoryArchiveCandidateSource[] { source }));

            for (int i = 0; i < 3; i++) Task03.StepNext(sim);
            Assert.That(sim.History.RecordCount, Is.EqualTo(3L));

            // 之后不再产生任何新候选：旧历史不得被读取/复制/哈希。
            source.SealedPerTick = 0;
            HistoryAccessCounters baseline = sim.HistoryAccessCounters;
            for (int i = 0; i < 8; i++) Task03.StepNext(sim);
            HistoryAccessCounters delta = sim.HistoryAccessCounters.DeltaFrom(baseline);

            Assert.That(delta.OldRecordReads, Is.EqualTo(0L), "无新增归档的 Tick：读取旧归档记录数必须为 0");
            Assert.That(delta.OldRecordCopies, Is.EqualTo(0L));
            Assert.That(delta.OldRecordHashes, Is.EqualTo(0L));
            Assert.That(delta.NewRecordsProcessed, Is.EqualTo(0L));
            Assert.That(delta.TotalAppends, Is.EqualTo(0L));
            Assert.That(sim.History.RecordCount, Is.EqualTo(3L));

            // 摘要也不得变化。
            Assert.That(sim.History.Digest, Is.EqualTo(HistorySummaryOf(sim, 3)));

            // 完全没有归档来源的标准装配同样保持 0 访问。
            var plain = Task03.NewSim();
            HistoryAccessCounters plainBaseline = plain.HistoryAccessCounters;
            for (int i = 0; i < 4; i++) Task03.StepNext(plain);
            HistoryAccessCounters plainDelta = plain.HistoryAccessCounters.DeltaFrom(plainBaseline);
            Assert.That(plainDelta.OldRecordReads + plainDelta.OldRecordCopies + plainDelta.OldRecordHashes,
                Is.EqualTo(0L));
        }

        private static string HistorySummaryOf(BattleSimulation sim, int recordCount)
            => sim.ReadHistoryPrefix(recordCount).Digest;

        [Test]
        public void NewArchiveBatchProcessesOnlyNewRecords()
        {
            var archive = new HistoryArchive();
            archive.AppendFinalizedOrdered(0L, new[] { Task03.Candidate(0L, HistoryRecordKind.WindowLedger, "w.0") });

            HistoryAccessCounters baseline = archive.AccessCounters;
            archive.AppendFinalizedOrdered(1L, new[]
            {
                Task03.Candidate(1L, HistoryRecordKind.WindowLedger, "w.1"),
                Task03.Candidate(1L, HistoryRecordKind.WindowLedger, "w.2"),
                Task03.Candidate(1L, HistoryRecordKind.WindowLedger, "w.3", false)
            });
            HistoryAccessCounters delta = archive.AccessCounters.DeltaFrom(baseline);

            Assert.That(delta.NewRecordsProcessed, Is.EqualTo(2L), "只处理本批新增记录");
            Assert.That(delta.IneligibleCandidatesSkipped, Is.EqualTo(1L));
            Assert.That(delta.OldRecordReads, Is.EqualTo(0L), "不得扫描/复制旧归档");
            Assert.That(delta.OldRecordCopies, Is.EqualTo(0L));
            Assert.That(delta.OldRecordHashes, Is.EqualTo(0L));
            Assert.That(archive.RecordCount, Is.EqualTo(3L));
        }

        [Test]
        public void OldSnapshotHistoryPrefixRemainsUnchangedAfterAppend()
        {
            var source = new FixtureArchiveSource { SealedPerTick = 1 };
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                archiveCandidateSources: new IHistoryArchiveCandidateSource[] { source }));

            StepResult early = Task03.StepNext(sim);
            HistorySummary oldSummary = early.Snapshot.History;
            string oldDigest = oldSummary.Digest;
            long oldCount = oldSummary.RecordCount;

            for (int i = 0; i < 3; i++) Task03.StepNext(sim);

            // 旧快照对象本身不持有可变归档引用：其摘要与记录数保持不变。
            Assert.That(early.Snapshot.History.RecordCount, Is.EqualTo(oldCount));
            Assert.That(early.Snapshot.History.Digest, Is.EqualTo(oldDigest));
            Assert.That(sim.History.RecordCount, Is.EqualTo(oldCount + 3L));

            // 旧快照对应的历史前缀仍然可由 RecordCount 精确定位，且摘要一致。
            HistoryDiagnosticView prefix = sim.ReadHistoryPrefix((int)oldCount);
            Assert.That(prefix.RecordCount, Is.EqualTo(oldCount));
            Assert.That(prefix.Digest, Is.EqualTo(oldDigest), "RecordCount 标识固定的历史前缀");

            // 旧快照的哈希也不得因为后续追加而改变（重新计算同一份旧快照）。
            Assert.That(early.Snapshot.ComputeHashHex(), Is.EqualTo(CanonicalHash.ToHex(early.Snapshot.ComputeHash())));
        }

        [Test]
        public void DiagnosticRecomputationDetectsAlteredOrMissingHistoryRecords()
        {
            var archive = new HistoryArchive();
            archive.AppendFinalizedOrdered(0L, new[]
            {
                Task03.Candidate(0L, HistoryRecordKind.ActionPlanTerminal, "plan.1", true, 1L),
                Task03.Candidate(1L, HistoryRecordKind.ActionPlanTerminal, "plan.2", true, 2L),
                Task03.Candidate(2L, HistoryRecordKind.WindowLedger, "window.1", true, 3L)
            });
            ulong authoritative = archive.Summary.DigestValue;

            IReadOnlyList<ArchivedRecord> copy = archive.CopyAllForDiagnostics();
            Assert.That(HistoryDigestProtocol.RecomputeFromSeed(copy), Is.EqualTo(authoritative));

            // 1. 篡改一条记录的载荷（只改测试副本）。
            var altered = copy.ToList();
            ArchivedRecord original = altered[1];
            altered[1] = new ArchivedRecord(original.ArchiveIndex, original.ArchivedAtTick, original.Kind,
                original.StableKey, Task03.Payload(999L), original.DigestAfterAppend);
            Assert.That(HistoryDigestProtocol.RecomputeFromSeed(altered), Is.Not.EqualTo(authoritative),
                "参照重算必须发现被改写的记录");

            // 2. 丢失一条记录。
            var missing = copy.ToList();
            missing.RemoveAt(2);
            Assert.That(HistoryDigestProtocol.RecomputeFromSeed(missing), Is.Not.EqualTo(authoritative),
                "参照重算必须发现丢失的记录");

            // 3. 改写稳定键或归档 Tick 同样被发现。
            var renamed = copy.ToList();
            ArchivedRecord second = renamed[0];
            renamed[0] = new ArchivedRecord(second.ArchiveIndex, second.ArchivedAtTick + 1L, second.Kind,
                second.StableKey, second.PayloadCopy(), second.DigestAfterAppend);
            Assert.That(HistoryDigestProtocol.RecomputeFromSeed(renamed), Is.Not.EqualTo(authoritative));

            // 4. 权威归档与增量摘要不受测试副本影响；诊断展开不写权威状态。
            Assert.That(archive.Summary.DigestValue, Is.EqualTo(authoritative));
            Assert.That(archive.RecomputeFullDigestForDiagnostics(), Is.EqualTo(authoritative));
            Assert.That(archive.AccessCounters.OldRecordReads, Is.GreaterThan(0L), "诊断读取要记入访问计数");
        }

        [Test]
        public void BattleEndArchivesFinalRecordsAndAlreadyEndedDoesNotAppend()
        {
            var source = new FixtureArchiveSource { SealedPerTick = 1 };
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                archiveCandidateSources: new IHistoryArchiveCandidateSource[] { source }));

            for (int i = 0; i < 3; i++) Task03.StepNext(sim);
            Assert.That(sim.History.RecordCount, Is.EqualTo(3L));

            sim.RequestStop("STOPPED_BY_FIXTURE");
            StepResult ended = Task03.StepNext(sim);

            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(ended.Snapshot.History.RecordCount, Is.EqualTo(4L), "结束 Tick 同样归档");
            Assert.That(ended.Snapshot.BattleEnd.IsEnded, Is.True);

            var endEvents = Task03.EventsOfType<BattleEndedEvent>(ended.Events).Cast<BattleEndedEvent>().ToList();
            Assert.That(endEvents.Count, Is.EqualTo(1));
            Assert.That(endEvents[0].ResultCode, Is.EqualTo("STOPPED_BY_FIXTURE"));
            Assert.That(ended.Events.Events[ended.Events.Count - 1], Is.SameAs(endEvents[0]),
                "BattleEndedEvent 必须是结束 Tick 的最后一个逻辑事件");

            HistorySummary finalSummary = ended.Snapshot.History;
            HistoryAccessCounters countersAtEnd = sim.HistoryAccessCounters;

            // 结束后误调用：不追加归档、不改摘要、不推进任何计数器。
            StepResult again = sim.Step(4L, sim.CommandIngress.FreezeTick(4L));
            Assert.That(again.Status, Is.EqualTo(StepStatus.AlreadyEnded));
            Assert.That(sim.History.RecordCount, Is.EqualTo(finalSummary.RecordCount));
            Assert.That(sim.History.Digest, Is.EqualTo(finalSummary.Digest));
            Assert.That(sim.HistoryAccessCounters.NewRecordsProcessed, Is.EqualTo(countersAtEnd.NewRecordsProcessed));
            Assert.That(sim.HistoryAccessCounters.TotalAppends, Is.EqualTo(countersAtEnd.TotalAppends));
        }

        [Test]
        public void HistoryEncodingChangeRequiresReplayFormatVersionChange()
        {
            // 1. 编码/格式版本护栏：摘要随 ReplayFormatVersion 变化。
            ulong seed1 = ReplayFormat.HistorySeedDigest(1);
            ulong seed2 = ReplayFormat.HistorySeedDigest(2);
            Assert.That(seed1, Is.Not.EqualTo(seed2), "初始种子必须绑定格式版本");

            ulong append1 = HistoryDigestProtocol.Append(seed1, 1L, 0L, HistoryRecordKind.WindowLedger, "w.1",
                Task03.Payload(1L), 1);
            ulong append2 = HistoryDigestProtocol.Append(seed1, 1L, 0L, HistoryRecordKind.WindowLedger, "w.1",
                Task03.Payload(1L), 2);
            Assert.That(append2, Is.Not.EqualTo(append1), "追加编码必须绑定格式版本");

            // 2. 类型键、字段顺序与长度前缀都在版本化描述里被显式冻结。
            string protocol = ReplayFormat.FrozenProtocolDescription();
            Assert.That(protocol, Does.Contain(ReplayFormat.HashAlgorithmId));
            Assert.That(protocol, Does.Contain("digest_hex_width=" + ReplayFormat.DigestHexWidth));
            Assert.That(protocol, Does.Contain(ReplayFormat.IntegerEncoding));
            Assert.That(protocol, Does.Contain(ReplayFormat.StringEncoding));
            Assert.That(protocol, Does.Contain(ReplayFormat.CollectionEncoding));
            Assert.That(protocol, Does.Contain(ReplayFormat.DomainEncoding));
            Assert.That(protocol, Does.Contain("record_kind=" + nameof(HistoryRecordKind.ActionPlanTerminal) + ":1"));
            Assert.That(protocol, Does.Contain("record_kind=" + nameof(HistoryRecordKind.WindowLedger) + ":2"));
            Assert.That(protocol, Does.Contain("record_kind=" + nameof(HistoryRecordKind.ReactionOpportunityBinding) + ":3"));
            Assert.That(protocol, Does.Contain("record_kind=" + nameof(HistoryRecordKind.CommandIngressBucket) + ":4"));

            // 3. 字段顺序变化必须改变摘要（域分离长度前缀编码的独立验证）。
            ulong ordered = HistoryDigestProtocol.Append(seed1, 1L, 7L, HistoryRecordKind.WindowLedger, "w.1",
                Task03.Payload(1L), 1);
            ulong tickChanged = HistoryDigestProtocol.Append(seed1, 1L, 8L, HistoryRecordKind.WindowLedger, "w.1",
                Task03.Payload(1L), 1);
            ulong keyChanged = HistoryDigestProtocol.Append(seed1, 1L, 7L, HistoryRecordKind.WindowLedger, "w.2",
                Task03.Payload(1L), 1);
            ulong kindChanged = HistoryDigestProtocol.Append(seed1, 1L, 7L, HistoryRecordKind.ActionPlanTerminal, "w.1",
                Task03.Payload(1L), 1);
            ulong payloadChanged = HistoryDigestProtocol.Append(seed1, 1L, 7L, HistoryRecordKind.WindowLedger, "w.1",
                Task03.Payload(2L), 1);
            ulong indexChanged = HistoryDigestProtocol.Append(seed1, 2L, 7L, HistoryRecordKind.WindowLedger, "w.1",
                Task03.Payload(1L), 1);
            Assert.That(new[] { tickChanged, keyChanged, kindChanged, payloadChanged, indexChanged },
                Has.None.EqualTo(ordered));

            // 4. 快照哈希同样绑定格式版本。
            var snapshot = Task03.NewSim().CurrentSnapshot;
            Assert.That(snapshot.ComputeHash(2), Is.Not.EqualTo(snapshot.ComputeHash(1)));
        }

        [Test]
        public void ScheduleRevisionAppearsInCanonicalSnapshot()
        {
            var sim = Task03.NewSim();
            StepResult result = Task03.StepEmpty(sim);

            Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(sim.ScheduleRevision));
            Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(0L));

            // ScheduleRevision 参与哈希：只改这一项的两份快照摘要不同。
            ulong baseline = SnapshotWithRevision(0L);
            Assert.That(SnapshotWithRevision(1L), Is.Not.EqualTo(baseline));
            Assert.That(SnapshotWithRevision(2L), Is.Not.EqualTo(SnapshotWithRevision(1L)));
        }

        private static ulong SnapshotWithRevision(long revision)
            => new LogicSnapshot(
                0L, "battle-def-v1", "a10fcfb98357418c", Task03.EncounterId.Value,
                BattleEndSnapshot.Active(), Array.Empty<UnitSnapshot>(), Array.Empty<StatusEffectSnapshot>(),
                TurnWindowManagerSnapshot.None(), ConcurrentActionSnapshot.None(), BattleResourceSnapshot.None(0),
                revision, Array.Empty<ActionPlanSnapshot>(), Array.Empty<ReactionOpportunitySnapshot>(),
                Array.Empty<ActorLaneSnapshot>(), Array.Empty<IntentSnapshot>(), Array.Empty<MovementSegmentSnapshot>(),
                Array.Empty<ReservationSnapshot>(), Array.Empty<AiControllerSnapshot>(), null,
                new RngSnapshot(DeterministicRng.AlgorithmVersion, Task03.Inputs.InitialRngSeed),
                3L, 1L, 1L, 1L, 1L, 1L, 1L, 1L, 1L, 1L, HistorySummary.Empty(),
                CommandSourcePriority.MappingVersion).ComputeHash();

        [Test]
        public void SnapshotHashBindsBattleEndSnapshot()
        {
            var active = Task03.NewSim();
            Task03.StepEmpty(active);

            var ended = Task03.NewSim();
            ended.RequestStop("RESULT_CODE_FIXTURE");
            StepResult endedResult = Task03.StepNext(ended);

            Assert.That(endedResult.Snapshot.BattleEnd.IsEnded, Is.True);
            Assert.That(endedResult.Snapshot.BattleEnd.EndedAtTick, Is.EqualTo(0L));
            Assert.That(endedResult.Snapshot.BattleEnd.ResultCode, Is.EqualTo("RESULT_CODE_FIXTURE"));
            Assert.That(endedResult.SnapshotHashHex, Is.Not.EqualTo(active.CurrentSnapshot.ComputeHashHex()),
                "BattleEndSnapshot 参与规范化哈希");

            // 结果码参与哈希。
            var otherCode = Task03.NewSim();
            otherCode.RequestStop("OTHER_RESULT_CODE");
            Assert.That(Task03.StepNext(otherCode).SnapshotHashHex, Is.Not.EqualTo(endedResult.SnapshotHashHex));
        }

        [Test]
        public void HistorySummaryStartsFromFixedSeedAndCountsOnlyAppends()
        {
            HistorySummary empty = HistorySummary.Empty();
            Assert.That(empty.RecordCount, Is.EqualTo(0L));
            Assert.That(empty.DigestValue, Is.EqualTo(ReplayFormat.HistorySeedDigest()));
            Assert.That(empty.Digest, Is.EqualTo(CanonicalHash.ToHex(ReplayFormat.HistorySeedDigest())));

            // RecordCount 不占用玩法 ID 或 EventSequence。
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                archiveCandidateSources: new IHistoryArchiveCandidateSource[] { new FixtureArchiveSource() }));
            Task03.StepNext(sim);
            Assert.That(sim.History.RecordCount, Is.EqualTo(1L));
            Assert.That(sim.CurrentSnapshot.NextEventSequence, Is.EqualTo(1L), "归档不消耗 EventSequence");
            Assert.That(sim.CurrentSnapshot.NextUnitId, Is.EqualTo(3L), "归档不消耗实例 ID");
            Assert.That(sim.CurrentSnapshot.NextActionPlanId, Is.EqualTo(1L));
        }
    }
}
