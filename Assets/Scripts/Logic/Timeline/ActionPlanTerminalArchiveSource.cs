using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 任务 05 的归档候选来源（接入任务 03 阶段 19 的
    /// <see cref="IHistoryArchiveCandidateSource"/>）。
    ///
    /// 契约（任务包「必须产出」4 的最后一段）：
    /// <list type="bullet">
    /// <item>候选<strong>只</strong>含本 Tick 新进入终态、且已完成全部清理的计划
    /// （由 <see cref="ActionPlanTerminalCoordinator.FrozenTickCandidates"/> 提供）；</item>
    /// <item>稳定键使用 <c>ActionPlanRegistry.StableKeyOf(plan)</c>
    /// （<c>ActionPlanId</c> 的十进制文本），与注册表的 <c>TerminalDigest</c> <strong>同源</strong>；</item>
    /// <item><c>ArchivedAtTick</c> = 计划的 <c>TerminalTick</c>；</item>
    /// <item>终态记录<strong>只追加一次</strong>：候选集合按 <c>ActionPlanId</c> 去重，
    /// 归档器自身也会以规范化键拒绝重复（<c>HISTORY_RECORD_DUPLICATE</c>）；</item>
    /// <item>无新增终态时返回空列表 ⇒ 归档器<strong>不读取、不复制、不哈希</strong>任何旧记录。</item>
    /// </list>
    ///
    /// ⚠️ <strong>载荷一致性（必须说明）</strong>：<see cref="ActionPlanRegistry.MarkTerminal"/>
    /// 在追加终态摘要时使用 <c>null</c> 载荷。为了让
    /// <c>LogicSnapshot.TerminalPlanDigest</c> 与 <c>HistoryArchive.Summary</c>
    /// 在同一批终态上得到<strong>相同</strong>的增量摘要，本来源同样使用 <c>null</c> 载荷。
    /// 改为规范字节载荷会改变历史摘要，因此它属于一次显式的
    /// <c>ReplayFormat.Version</c> 变更，不属于任务 05。
    /// </summary>
    public sealed class ActionPlanTerminalArchiveSource : IHistoryArchiveCandidateSource
    {
        private readonly ActionScheduleAuthority _authority;
        private readonly ActionPlanTerminalCoordinator _coordinator;

        public ActionPlanTerminalArchiveSource(
            ActionScheduleAuthority authority, ActionPlanTerminalCoordinator coordinator)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public IReadOnlyList<HistoryArchiveCandidate> CollectOrdered(long tick)
        {
            IReadOnlyList<ActionPlanId> candidates = _coordinator.FrozenTickCandidates;
            if (candidates == null || candidates.Count == 0) return Array.Empty<HistoryArchiveCandidate>();

            var records = new List<HistoryArchiveCandidate>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                ActionPlan plan = _authority.Registry.Find(candidates[i]);
                if (plan == null || !plan.IsTerminal) continue;

                records.Add(new HistoryArchiveCandidate(
                    plan.TerminalTick,
                    HistoryRecordKind.ActionPlanTerminal,
                    ActionPlanRegistry.StableKeyOf(plan),
                    null,
                    HistorySealProof.Sealed));
            }

            records.Sort((a, b) => a.CompareCanonical(b));
            return records;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public static string Describe(IReadOnlyList<HistoryArchiveCandidate> candidates)
            => candidates == null
                ? "<null>"
                : "terminal-archive-candidates=" + candidates.Count.ToString(CultureInfo.InvariantCulture);
    }
}
