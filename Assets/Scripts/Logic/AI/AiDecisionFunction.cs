using System;
using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Determinism;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong>AI 决策函数</strong>（纯函数面）：相同上下文 + 相同 seed ⇒ <strong>逐字段相同</strong>的
    /// <see cref="AiDecision"/>。
    ///
    /// 它<strong>不</strong>持有状态、<strong>不</strong>写任何权威状态、<strong>不</strong>分配
    /// <c>ProducerOrdinal</c>/<c>CommandSequence</c>，也<strong>不</strong>提交命令——
    /// 提交只发生在 <see cref="AiControllerLogic"/> 的观察回调里，且只能经已注册入口。
    ///
    /// <para>
    /// 决策顺序<strong>不可交换</strong>：先构建并<strong>稳定排序</strong>全部普通候选与反应候选
    /// （全程不碰 RNG），<strong>只有排序完成后</strong>才用版本化
    /// <see cref="DeterministicRng"/> 做选择。
    /// </para>
    /// </summary>
    public static class AiDecisionFunction
    {
        /// <summary>没有任何窗口时「关闭窗口命令」不可用（AI 只能申请关闭自己的窗口）。</summary>
        public const string ReasonNoOwnWindow = "NO_OWN_WINDOW";

        public const string ReasonNoEligibleCandidate = "NO_ELIGIBLE_CANDIDATE";
        public const string ReasonNoReactionAvailable = "NO_REACTION_AVAILABLE";

        /// <summary>
        /// 版本化 RNG 种子推导：<c>splitmix64(seed ^ mix(controllerId) ^ mix(tick))</c>。
        /// 它<strong>不</strong>读时间、帧计数、<c>GetHashCode</c> 或文化，因此跨平台逐位稳定。
        /// </summary>
        public static ulong DeriveSeed(ulong battleSeed, string controllerId, long tick)
        {
            unchecked
            {
                ulong mixed = battleSeed;
                mixed ^= Mix64(StableStringHash(controllerId));
                mixed ^= Mix64((ulong)tick * 0x9E3779B97F4A7C15UL);
                return Mix64(mixed);
            }
        }

        /// <summary>与平台、文化无关的稳定 64 位字符串哈希（FNV-1a 的 64 位形态）。</summary>
        public static ulong StableStringHash(string value)
        {
            unchecked
            {
                ulong hash = 0xCBF29CE484222325UL;
                if (value == null) return hash;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 0x100000001B3UL;
                }
                return hash;
            }
        }

        private static ulong Mix64(ulong z)
        {
            unchecked
            {
                z += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>
        /// 决策主入口。返回的 <see cref="AiDecision.Selected"/> 是<strong>唯一</strong>可提交物。
        /// </summary>
        public static AiDecision Decide(AiDecisionContext context, ulong battleSeed)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Snapshot == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_REQUEST_NULL, "AI decision context requires a decision snapshot");

            long tick = context.Snapshot.Tick;
            var rng = new Determinism.DeterministicRng(
                DeriveSeed(battleSeed, context.ControllerId.Value, tick));

            // —— 第 1 步：普通动作候选（先构建 + 稳定排序，不使用 RNG）——
            IReadOnlyList<AiCandidate> normal = AiCandidateBuilder.BuildNormalCandidatesOrdered(context);

            // —— 第 2 步：反应候选（先构建 + 稳定排序，不使用 RNG）——
            IReadOnlyList<AiReactionOptionView> reaction =
                AiReactionCandidateBuilder.BuildReactionCandidatesOrdered(context, tick);

            // —— 第 3 步：只有此刻才允许使用 RNG ——
            // 反应优先于普通动作：反应选项有截止 Tick，错过即永久失去；普通排程可在下一 Tick 重提。
            string reason = null;
            CommandRequest selected = SelectReaction(context, reaction, rng, out string reactionReason);
            if (selected == null)
            {
                reason = reactionReason;
                selected = SelectNormal(context, normal, rng, out string normalReason);
                if (selected == null) reason = normalReason;
            }

            return new AiDecision(selected, normal, reaction, rng.CaptureSnapshot(),
                normal.Count, reaction.Count, reason, tick);
        }

        /// <summary>
        /// 对<strong>已经稳定排序</strong>的反应候选做选择（排序之后的那一步）。
        ///
        /// 它与 <see cref="Decide"/> 共用同一个 RNG 派生与同一个 <c>PickIndex</c> 语义，
        /// 因此"排列 → 排序 → 选择"三段可以各自被独立断言：排序后的候选相同 ⇒ 选择必然相同。
        /// </summary>
        /// <param name="orderedReactions">已按 <c>CompareStableKeyTo</c> 排序的反应候选。</param>
        /// <param name="battleSeed">本场 RNG 种子（与 <see cref="Decide"/> 同源）。</param>
        /// <param name="tick">用于派生 RNG 的快照 Tick。</param>
        /// <param name="targetTick">产出的命令目标 Tick（正常路径 = 快照 Tick + 1）。</param>
        /// <param name="controllerId">控制者身份（参与 RNG 派生，与 <see cref="Decide"/> 同源）。</param>
        public static AiDecision DecideOrderedReactions(
            IReadOnlyList<AiReactionOptionView> orderedReactions, ulong battleSeed, long tick,
            long targetTick, string controllerId)
        {
            var rng = new Determinism.DeterministicRng(DeriveSeed(battleSeed, controllerId, tick));
            CommandRequest selected = SelectReactionFromOrdered(orderedReactions, rng, targetTick);
            return new AiDecision(selected,
                Array.Empty<AiCandidate>(), orderedReactions, rng.CaptureSnapshot(),
                0, orderedReactions?.Count ?? 0,
                selected == null ? ReasonNoReactionAvailable : null, tick);
        }

        // ================= 选择（唯一 RNG 使用点） =================

        private static CommandRequest SelectNormal(
            AiDecisionContext context, IReadOnlyList<AiCandidate> ordered,
            Determinism.DeterministicRng rng, out string reason)
        {
            var eligible = new List<AiCandidate>();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].IsEligible) eligible.Add(ordered[i]);
            }
            if (eligible.Count == 0)
            {
                reason = ReasonNoEligibleCandidate;
                return null;
            }

            AiCandidate chosen = eligible[rng.PickIndex(eligible.Count)];
            reason = null;
            return AiCommandRequestBuilder.ToRequest(context, chosen);
        }

        private static CommandRequest SelectReaction(
            AiDecisionContext context, IReadOnlyList<AiReactionOptionView> ordered,
            Determinism.DeterministicRng rng, out string reason)
        {
            if (ordered.Count == 0)
            {
                reason = ReasonNoReactionAvailable;
                return null;
            }

            // AI 对每个机会可以选择不响应 / Block / Dodge；首版评分只用稳定键顺序，
            // 不做"完美命中时机"（时机恒由机会从来源 ImpactTick 统一推导）。
            // 选中的**只会**是一条标准 ReactionCommand，经处理器 → ReactionPlanner。
            reason = null;
            return SelectReactionFromOrdered(ordered, rng, context.NextTick);
        }

        private static CommandRequest SelectReactionFromOrdered(
            IReadOnlyList<AiReactionOptionView> ordered, Determinism.DeterministicRng rng, long targetTick)
        {
            if (ordered == null || ordered.Count == 0) return null;
            AiReactionOptionView chosen = ordered[rng.PickIndex(ordered.Count)];
            return new CommandRequest(targetTick,
                new ReactionCommandScope(chosen.ReactionOpportunityId),
                new ReactionCommandPayload(chosen.ReactionKind, chosen.ActionSpecId, chosen.SelectedDestination));
        }
    }
}
