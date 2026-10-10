using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Replay;
using System.Text;

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
    /// 格式 v4 保存完整、深度不可变的最终计划投影（含时序、scope、预算和终止原因）。
    /// 注册表 TerminalPlanDigest 仍是终态键/时间的计数索引摘要；完整记录由
    /// LogicSnapshot.History 的摘要覆盖，两者不再要求相等。冻结只发生在清理完成的阶段 19。
    /// </summary>
    public sealed class ActionPlanTerminalArchiveSource : IHistoryArchiveCandidateSource
    {
        private readonly ActionScheduleAuthority _authority;
        private readonly ActionPlanTerminalCoordinator _coordinator;
        private readonly IFactionRelationResolver _factions;

        public ActionPlanTerminalArchiveSource(
            ActionScheduleAuthority authority, ActionPlanTerminalCoordinator coordinator, IFactionRelationResolver factions = null)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _factions = factions;
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

                var snapshot = _authority.Registry.FreezeTerminal(plan, _factions);
                var encoder = new CanonicalEncoder();
                encoder.BeginDomain("ActionPlanTerminal.v4");
                encoder.WriteBytes(Encoding.UTF8.GetBytes(ReplayEventComparison.CanonicalValue(snapshot)));
                records.Add(new HistoryArchiveCandidate(
                    plan.TerminalTick,
                    HistoryRecordKind.ActionPlanTerminal,
                    ActionPlanRegistry.StableKeyOf(plan),
                    encoder.ToArray(),
                    HistorySealProof.Sealed));
            }

            records.Sort((a, b) => a.CompareCanonical(b));
            _coordinator.MarkArchiveCandidatesConsumed();
            return records;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public static string Describe(IReadOnlyList<HistoryArchiveCandidate> candidates)
            => candidates == null
                ? "<null>"
                : "terminal-archive-candidates=" + candidates.Count.ToString(CultureInfo.InvariantCulture);
    }
}
