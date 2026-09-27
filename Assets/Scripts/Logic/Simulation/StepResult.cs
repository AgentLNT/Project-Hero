using System;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Simulation
{
    /// <summary>Step 结果状态（任务 03「必须产出」9）。</summary>
    public enum StepStatus
    {
        /// <summary>活动战斗正常推进了一个 Tick。</summary>
        Advanced = 0,

        /// <summary>本 Tick 检测到胜负并完成唯一终态清理；该结果携带结束 Tick 的最终事件与快照。</summary>
        BattleEnded = 1,

        /// <summary>战斗已经结束后的误调用：不推进任何状态，返回空事件批次与缓存的最终快照。</summary>
        AlreadyEnded = 2
    }

    /// <summary>
    /// 一次 <c>Step</c> 的不可变结果。
    ///
    /// 它<strong>只</strong>暴露三样东西：状态码、只读事件批次、规范化快照。
    /// 不暴露 Logic 内部集合、不暴露任何写接口、不暴露可变对象的可变成员；
    /// 调用者不能在 Step 之后回写逻辑状态。
    /// </summary>
    public sealed class StepResult
    {
        public StepResult(StepStatus status, EventBatch events, LogicSnapshot snapshot)
        {
            Status = status;
            Events = events ?? throw new ArgumentNullException(nameof(events));
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public StepStatus Status { get; }

        /// <summary>该 Tick 全部逻辑提交完成之后一次性可见的事件批次（空 Tick 也非 null）。</summary>
        public EventBatch Events { get; }

        /// <summary>规范化观察快照（不是恢复输入）。</summary>
        public LogicSnapshot Snapshot { get; }

        public bool IsAdvanced => Status == StepStatus.Advanced;
        public bool IsBattleEnded => Status == StepStatus.BattleEnded;
        public bool IsAlreadyEnded => Status == StepStatus.AlreadyEnded;

        /// <summary>本结果的 Tick（= <see cref="LogicSnapshot.Tick"/>）。</summary>
        public long Tick => Snapshot.Tick;

        public ulong SnapshotHash => Snapshot.ComputeHash();

        public string SnapshotHashHex => Snapshot.ComputeHashHex();
    }
}
