using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Determinism;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong>一次 AI 决策的产出</strong>：<see cref="Selected"/> 可能是 <c>null</c>
    /// （本轮合法地不提出任何命令）。
    ///
    /// 它是<strong>唯一</strong>可提交物，且只能是一条 <see cref="CommandRequest"/>：
    /// AI 无法产出 Envelope、机会、计划或任何序号，也无法绕过 <c>ScheduleRevision</c>。
    /// <see cref="NormalCandidates"/> / <see cref="ReactionCandidates"/> 是排序之后参与选择的
    /// <strong>冻结诊断面</strong>（证明"候选排列不同但同 seed 选择相同"），<strong>不</strong>参与哈希。
    /// </summary>
    public sealed record AiDecision(
        CommandRequest Selected,
        IReadOnlyList<AiCandidate> NormalCandidates,
        IReadOnlyList<AiReactionOptionView> ReactionCandidates,
        RngSnapshot Rng,
        int NormalCandidateCount,
        int ReactionCandidateCount,
        string NoSelectionReason,
        long DecisionTick = -1L)
    {
        public static AiDecision None(RngSnapshot rng, string reason)
            => new AiDecision(null, Array.Empty<AiCandidate>(), Array.Empty<AiReactionOptionView>(),
                rng, 0, 0, reason ?? "<none>");

        /// <summary>本轮是否提出了命令。</summary>
        public bool HasSelection => Selected != null;

        public override string ToString()
            => "normal=" + NormalCandidateCount.ToString(CultureInfo.InvariantCulture) +
               " reaction=" + ReactionCandidateCount.ToString(CultureInfo.InvariantCulture) +
               " selected=" + (Selected == null ? "<none:" + NoSelectionReason + ">" : CommandScopes.Describe(Selected.Scope));
    }
}
