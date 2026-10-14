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
