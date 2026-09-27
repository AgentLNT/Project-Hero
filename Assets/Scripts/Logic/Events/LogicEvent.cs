using System;
using System.Collections.Generic;
using ProjectHero.Logic.Determinism;

namespace ProjectHero.Logic.Events
{
    /// <summary>
    /// 不可变逻辑事件基类（主方案 3.3.1）。事件只描述<strong>已经提交的战斗事实</strong>：
    /// <list type="bullet">
    /// <item>只携带不可变值与稳定 ID，绝不持有逻辑单位、MonoBehaviour、ScriptableObject 或视图引用；</item>
    /// <item>不携带震屏强度、顿帧秒数、Animator 参数、插值时长等表现策略
    /// （00 号规则 10 / 任务包「禁止事项」）；</item>
    /// <item><see cref="Sequence"/> 整场战斗单调递增，不按 Tick 清零；</item>
    /// <item>一个 Tick 的全部事件在该 Tick <strong>全部逻辑提交完成之后</strong>一次性对外可见
    /// （<see cref="EventBatch"/>），表现订阅不能在 Step 中回写。</item>
    /// </list>
    /// </summary>
    public abstract record LogicEvent(long Tick, long Sequence);

    /// <summary>
    /// 一个 Tick 的事件批次（不可变、只读）。空 Tick 也必须返回批次
    /// （<see cref="Empty"/>），而不是 null。
    /// </summary>
    public sealed record EventBatch(long Tick, IReadOnlyList<LogicEvent> Events)
    {
        public static EventBatch Empty(long tick) => new EventBatch(tick, Array.Empty<LogicEvent>());

        public int Count => Events == null ? 0 : Events.Count;

        /// <summary>按 <see cref="LogicEvent.Sequence"/> 升序的只读事件列表（批次本身就是该顺序）。</summary>
        public IReadOnlyList<LogicEvent> EventsInSequenceOrder => Events ?? Array.Empty<LogicEvent>();
    }
    /// <summary>
    /// 事件 Outbox（主方案 3.3.1）。事件序号来自整场唯一的
    /// <see cref="LogicSequenceGenerator.NextEventSequence"/>，因此
    /// <see cref="NextEventSequence"/> 必须进入规范化快照与哈希。
    ///
    /// <see cref="Emit"/> 在 Tick 内累积；<see cref="Flush"/> 只在 Tick 末调用一次，
    /// 返回按序号升序的不可变批次并清空缓冲。
    /// </summary>
    public sealed class LogicEventOutbox
    {
        private readonly List<LogicEvent> _current = new List<LogicEvent>();
        private readonly LogicSequenceGenerator _sequences;

        public LogicEventOutbox(LogicSequenceGenerator sequences)
        {
            _sequences = sequences ?? throw new ArgumentNullException(nameof(sequences));
        }

        /// <summary>整场战斗下一个事件序号（单调递增，永不按 Tick 清零）。</summary>
        public long NextEventSequence => _sequences.NextEventSequence;

        public int PendingCount => _current.Count;

        /// <summary>
        /// 发射一个事件；工厂收到<strong>本场唯一</strong>的事件序号。
        /// 事件携带的 Tick 由事件自身声明（PhaseRunner 传入当前 Tick）。
        /// </summary>
        public void Emit(Func<long, LogicEvent> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            long sequence = _sequences.TakeEventSequence();
            LogicEvent logicEvent = factory(sequence);
            if (logicEvent == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    "LOGIC_EVENT_FACTORY_RETURNED_NULL", "sequence=" + sequence);
            _current.Add(logicEvent);
        }

        /// <summary>Tick 末一次性对外可见；返回后缓冲清空，事件序号不回卷。</summary>
        public EventBatch Flush(long tick)
        {
            var events = _current.ToArray();
            _current.Clear();
            // 只读包装：调用者无法把批次当成可写集合使用，也不能通过数组别名改写已发布事件。
            return new EventBatch(tick, Array.AsReadOnly(events));
        }

        /// <summary>诊断用：尚未 Flush 的事件数量与顺序（不改变状态）。</summary>
        public IReadOnlyList<LogicEvent> PeekPending() => _current.ToArray();
    }
}
