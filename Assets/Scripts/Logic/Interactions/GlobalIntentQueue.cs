using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 本 Tick 冻结后的全局 Intent 队列（任务 08「必须产出」4）。
    ///
    /// 取出本 Tick <strong>所有到期</strong> Intent，<strong>不按</strong> <c>SubmittedWindowId</c>
    /// 或当前 <c>WindowId</c> 过滤（不变量 5：「仲裁读取本 Tick 全部有效动作，不按提交窗口过滤；
    /// 过去窗口提交的动作可与当前及未来窗口动作交互」）。
    ///
    /// 结构保证：<see cref="GlobalIntentQueue.Freeze"/> <strong>没有</strong>任何窗口参数 ——
    /// 调用方无法把窗口信息带进来，也就不存在"忘记去掉过滤"的写法。
    /// </summary>
    public sealed class FrozenIntentQueue
    {
        private readonly CombatIntent[] _intents;

        internal FrozenIntentQueue(long tick, CombatIntent[] intents)
        {
            Tick = tick;
            _intents = intents ?? Array.Empty<CombatIntent>();
        }

        /// <summary>本队列所属的逻辑 Tick。</summary>
        public long Tick { get; }

        /// <summary>稳定排序后的到期 Intent：<c>(Tick, 交互优先级, IntentSequence)</c> 升序。</summary>
        public IReadOnlyList<CombatIntent> Intents => _intents;

        public int Count => _intents.Length;

        /// <summary>规范序列化（排列不变性断言的抓手之一）。</summary>
        public string CanonicalText
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("queue|tick=").Append(Tick.ToString(CultureInfo.InvariantCulture))
                  .Append("|count=").Append(_intents.Length.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < _intents.Length; i++)
                {
                    sb.Append('\n').Append(_intents[i].CanonicalText);
                }
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// 全局 Intent 队列入口。稳定排序键为 <c>Tick → InteractionPriority → IntentSequence</c>
    /// （<c>BattleSimulation.cs:1833</c> 的阶段注释与冻结清单 §1.4 一致）。
    ///
    /// <strong>为什么该键与输入顺序无关</strong>：三个分量都是 Intent 自身携带的不可变值，
    /// 与它在集合中的位置无关；<c>IntentSequence</c> 由逻辑层单调分配器保证全局唯一
    /// （<see cref="CombatIntentContract.ValidateProducedOnce"/> 显式校验），因此该键是<strong>全序</strong>，
    /// 排序结果是输入<em>多重集合</em>的纯函数。任何输入排列都得到逐元素相同的输出序列。
    /// </summary>
    public static class GlobalIntentQueue
    {
        /// <summary>
        /// 取出本 Tick 全部到期 Intent 并冻结。
        ///
        /// 到期判定只有一处：<c>ImpactTick == tick</c>。刻意<strong>不</strong>检查
        /// <c>SubmittedWindowId</c>、当前窗口、单位状态或 <c>CanReceiveDirectHit</c>。
        /// 入参 <paramref name="producedIntents"/> 的数组顺序对结果没有任何影响。
        /// </summary>
        public static FrozenIntentQueue Freeze(long tick, IReadOnlyList<CombatIntent> producedIntents)
        {
            if (producedIntents == null || producedIntents.Count == 0)
            {
                return new FrozenIntentQueue(tick, Array.Empty<CombatIntent>());
            }

            var due = new List<CombatIntent>(producedIntents.Count);
            for (int i = 0; i < producedIntents.Count; i++)
            {
                CombatIntent intent = producedIntents[i];
                if (intent == null) continue;
                if (intent.ImpactTick != tick) continue;
                due.Add(intent);
            }

            CombatIntent[] ordered = due.ToArray();
            CombatIntentContract.ValidateProducedOnce(ordered);
            Array.Sort(ordered, CompareByStableKey);
            return new FrozenIntentQueue(tick, ordered);
        }

        /// <summary>稳定仲裁键：Tick → 交互优先级 → IntentSequence（<c>CompareTo</c> 语义，越小越先）。</summary>
        public static int CompareByStableKey(CombatIntent left, CombatIntent right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;

            int byTick = left.OrderTick.CompareTo(right.OrderTick);
            if (byTick != 0) return byTick;

            int byPriority = left.InteractionPriority.CompareTo(right.InteractionPriority);
            if (byPriority != 0) return byPriority;

            return left.IntentSequence.CompareTo(right.IntentSequence);
        }
    }
}
