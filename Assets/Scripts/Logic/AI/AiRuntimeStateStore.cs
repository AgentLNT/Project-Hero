using System.Collections.Generic;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// 一个 AI 控制者的<strong>可变运行态</strong>（唯一写者是 <see cref="AiControllerLogic"/> 的观察回调）。
    ///
    /// 它<strong>不</strong>是快照类型：对外只经
    /// <see cref="AiControllerLogic.CaptureRuntimeStatesOrdered"/> 投影成不可变的
    /// <see cref="AiControllerRuntimeState"/>，因此"UI/驱动器现场改 AI 状态"在类型上不存在。
    /// </summary>
    internal sealed class AiRuntimeState
    {
        public AiRuntimeState(string controllerId)
        {
            ControllerId = controllerId;
            // 开局即可决策：阶段 18 的第一次投递发生在 Tick 0。
            NextThinkTick = 0L;
            LastDecisionTick = -1L;
        }

        public string ControllerId { get; }

        /// <summary>累计决策次数（进入规范化快照与哈希）。</summary>
        public long DecisionCount { get; set; }

        /// <summary>最后一次已完成决策所属的快照 Tick（从未决策 = -1）。</summary>
        public long LastDecisionTick { get; set; }

        /// <summary>下一次允许决策的 Tick（= 上一条命令的目标 Tick）。</summary>
        public long NextThinkTick { get; set; }

        /// <summary>最后一次决策（诊断面；其 <c>Rng</c> 状态进入快照）。</summary>
        public AiDecision LastDecision { get; set; }

        /// <summary>投影成不可变的规范化运行态。</summary>
        public AiControllerRuntimeState ToSnapshot()
            => new AiControllerRuntimeState(
                ControllerId, DecisionCount, LastDecisionTick, NextThinkTick,
                LastDecision == null ? null : LastDecision.Rng);
    }
}
