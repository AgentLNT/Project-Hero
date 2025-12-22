using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;

namespace ProjectHero.Core.Timeline
{
    public class BattleTimeline : MonoBehaviour
    {
        // 逻辑帧率：60 Ticks per second
        public const int TicksPerSecond = 60;
        public const float SecondsPerTick = 1f / TicksPerSecond;

        // 当前逻辑帧 (离散)
        public long CurrentTick { get; private set; } = 0;

        // 逻辑时间 (阶梯状，用于逻辑计算)
        public float CurrentTime => CurrentTick * SecondsPerTick;

        // 视觉时间 (线性插值，用于渲染平滑)
        // = 已固定的逻辑时间 + 当前帧积累的残余时间
        public float VisualTime => (CurrentTick * SecondsPerTick) + _timeAccumulator;

        public bool Paused { get; private set; } = false;

        public System.Action<CombatUnit> OnDodgeSuccessRequestMove;

        private float _timeAccumulator = 0f;
        private int _sequenceCounter = 0; // 保证同一帧、同优先级的事件按插入顺序执行

        private struct ScheduledIntent
        {
            public long Id;
            public long GroupId;
            public long Tick; // 发生的确切逻辑帧
            public int Priority;
            public int InsertSequence; // 稳定性保证
            public CombatIntent Intent;
            public string Description;
        }

        private List<ScheduledIntent> _events = new List<ScheduledIntent>();
        private List<CombatIntent> _frameIntents = new List<CombatIntent>();

        private long _nextEventId = 1;
        private long _nextGroupId = 1;

        public long ReserveGroupId() => _nextGroupId++;

        public void SetPaused(bool paused)
        {
            Paused = paused;
        }

        public void RequestDodgeCounterMove(CombatUnit unit)
        {
            OnDodgeSuccessRequestMove?.Invoke(unit);
        }

        public void TriggerSlowMotion(float scale, float durationRealtime)
        {
            StartCoroutine(DoSlowMotion(scale, durationRealtime));
        }

        private System.Collections.IEnumerator DoSlowMotion(float scale, float duration)
        {
            Time.timeScale = scale;
            yield return new WaitForSecondsRealtime(duration);
            Time.timeScale = 1.0f;
        }

        /// <summary>
        /// 调度方法：基于 Tick 和 Priority
        /// </summary>
        public long Schedule(float delaySeconds, CombatIntent intent, string description = null, long groupId = 0, int priority = 0)
        {
            // 将秒转换为帧
            int delayTicks = Mathf.Max(0, Mathf.RoundToInt(delaySeconds * TicksPerSecond));
            long targetTick = CurrentTick + delayTicks;

            long id = _nextEventId++;

            _events.Add(new ScheduledIntent
            {
                Id = id,
                GroupId = groupId,
                Tick = targetTick,
                Priority = priority,
                InsertSequence = _sequenceCounter++,
                Intent = intent,
                Description = description ?? intent.ToString()
            });

            SortEvents();
            return id;
        }

        private void SortEvents()
        {
            _events.Sort((a, b) =>
            {
                // 1. 时间 (Tick) 早的在前
                if (a.Tick != b.Tick) return a.Tick.CompareTo(b.Tick);

                // 2. 优先级 (Priority) 高的在前 (数值大先执行)
                if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);

                // 3. 插入顺序 (Sequence) 早的在前
                return a.InsertSequence.CompareTo(b.InsertSequence);
            });
        }

        public void CancelGroup(long groupId)
        {
            if (groupId == 0) return;
            // 处理 Commit 撤销逻辑
            foreach (var evt in _events)
            {
                if (evt.GroupId == groupId && evt.Intent is ProjectHero.Core.Actions.Intents.CommitMoveStepIntent commit)
                {
                    commit.ReleaseReservation();
                }
            }
            _events.RemoveAll(e => e.GroupId == groupId);
        }

        public void CancelEvents(CombatUnit unit)
        {
            foreach (var evt in _events)
            {
                if (evt.Intent != null && evt.Intent.Owner == unit && evt.Intent is ProjectHero.Core.Actions.Intents.CommitMoveStepIntent commit)
                {
                    commit.ReleaseReservation();
                }
            }
            _events.RemoveAll(e => e.Intent != null && e.Intent.Owner == unit);
        }

        public void AdvanceTime(float deltaTimeReal)
        {
            if (Paused) return;

            _timeAccumulator += deltaTimeReal;

            // 固定步长更新 (Fixed Time Step)
            while (_timeAccumulator >= SecondsPerTick)
            {
                _timeAccumulator -= SecondsPerTick;
                ProcessTick(CurrentTick);
                CurrentTick++;
            }
        }

        private void ProcessTick(long tick)
        {
            _frameIntents.Clear();
            _sequenceCounter = 0; // 重置每帧的序列计数器

            // 1. 提取所有属于当前帧（或过期未执行）的事件
            while (_events.Count > 0 && _events[0].Tick <= tick)
            {
                var evt = _events[0];
                _events.RemoveAt(0);
                _frameIntents.Add(evt.Intent);
            }

            // 2. 仲裁 (Arbiter)
            if (_frameIntents.Count > 0)
            {
                CombatArbiter.Resolve(_frameIntents, this);
            }

            // 3. 执行结果
            foreach (var intent in _frameIntents)
            {
                if (!intent.IsCancelled)
                {
                    intent.ExecuteSuccess();
                }
            }
        }

        // --- UI 辅助 ---

        public struct ScheduledIntentInfo
        {
            public long Id;
            public long GroupId;
            public float Time;
            public CombatUnit Owner;
            public ActionType Type;
            public string Description;
        }

        public List<ScheduledIntentInfo> GetScheduledIntentsSnapshot()
        {
            return _events.Select(e => new ScheduledIntentInfo
            {
                Id = e.Id,
                GroupId = e.GroupId,
                Time = e.Tick * SecondsPerTick, // 显示时转回秒
                Owner = e.Intent?.Owner,
                Type = e.Intent?.Type ?? ActionType.None,
                Description = e.Description
            }).ToList();
        }
    }
}
