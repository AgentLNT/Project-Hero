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
        // �߼�֡�ʣ�60 Ticks per second
        public const int TicksPerSecond = 60;
        public const float SecondsPerTick = 1f / TicksPerSecond;

        // ��ǰ�߼�֡ (��ɢ)
        public long CurrentTick { get; private set; } = 0;

        // �߼�ʱ�� (����״�������߼�����)
        public float CurrentTime => CurrentTick * SecondsPerTick;

        // �Ӿ�ʱ�� (���Բ�ֵ��������Ⱦƽ��)
        // = �ѹ̶����߼�ʱ�� + ��ǰ֡���۵Ĳ���ʱ��
        public float VisualTime => (CurrentTick * SecondsPerTick) + _timeAccumulator;

        private bool _userPaused = false;
        private bool _systemPaused = false;

        public bool Paused => _userPaused || _systemPaused;
        public bool SystemPaused => _systemPaused;

        public System.Action OnScheduleChanged;
        public System.Action<long, System.Collections.Generic.IReadOnlyList<CombatIntent>> OnTickProcessed;

        public System.Action<CombatUnit> OnDodgeSuccessRequestMove;

        private float _timeAccumulator = 0f;
        private int _sequenceCounter = 0; // ��֤ͬһ֡��ͬ���ȼ����¼�������˳��ִ��

        private struct ScheduledIntent
        {
            public long Id;
            public long GroupId;
            public long Tick; // ������ȷ���߼�֡
            public int Priority;
            public int InsertSequence; // �ȶ��Ա�֤
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
            _userPaused = paused;
        }

        public void SetSystemPaused(bool paused)
        {
            _systemPaused = paused;
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
        /// ���ȷ��������� Tick �� Priority
        /// </summary>
        public long Schedule(float delaySeconds, CombatIntent intent, string description = null, long groupId = 0, int priority = 0)
        {
            // ����ת��Ϊ֡
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
            OnScheduleChanged?.Invoke();
            return id;
        }

        private void SortEvents()
        {
            _events.Sort((a, b) =>
            {
                // 1. ʱ�� (Tick) �����ǰ
                if (a.Tick != b.Tick) return a.Tick.CompareTo(b.Tick);

                // 2. ���ȼ� (Priority) �ߵ���ǰ (��ֵ����ִ��)
                if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);

                // 3. ����˳�� (Sequence) �����ǰ
                return a.InsertSequence.CompareTo(b.InsertSequence);
            });
        }

        public void CancelGroup(long groupId)
        {
            if (groupId == 0) return;
            // ���� Commit �����߼�
            foreach (var evt in _events)
            {
                if (evt.GroupId == groupId && evt.Intent is ProjectHero.Core.Actions.Intents.CommitMoveStepIntent commit)
                {
                    commit.ReleaseReservation();
                }
            }
            _events.RemoveAll(e => e.GroupId == groupId);
            OnScheduleChanged?.Invoke();
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
            OnScheduleChanged?.Invoke();
        }

        public void AdvanceTime(float deltaTimeReal)
        {
            if (Paused) return;

            _timeAccumulator += deltaTimeReal;

            // �̶��������� (Fixed Time Step)
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
            _sequenceCounter = 0; // ����ÿ֡�����м�����

            // 1. ��ȡ�������ڵ�ǰ֡�������δִ�У����¼�
            while (_events.Count > 0 && _events[0].Tick <= tick)
            {
                var evt = _events[0];
                _events.RemoveAt(0);
                _frameIntents.Add(evt.Intent);
            }

            // 2. �ٲ� (Arbiter)
            if (_frameIntents.Count > 0)
            {
                CombatArbiter.Resolve(_frameIntents, this);
            }

            // 3. ִ�н��
            foreach (var intent in _frameIntents)
            {
                if (!intent.IsCancelled)
                {
                    intent.ExecuteSuccess();
                }
            }

            if (_frameIntents.Count > 0)
            {
                // Copy to avoid subscribers observing list reuse across ticks.
                var processed = _frameIntents.ToArray();
                OnTickProcessed?.Invoke(tick, processed);
            }
        }

        // --- UI ���� ---

        public struct ScheduledIntentInfo
        {
            public long Id;
            public long GroupId;
            public float Time;
            public CombatUnit Owner;
            public ActionType Type;
            public string Description;
        }

        public struct ScheduledIntentDetailedInfo
        {
            public long Id;
            public long GroupId;
            public long Tick;
            public float Time;
            public int Priority;
            public CombatIntent Intent;
            public string Description;
        }

        public List<ScheduledIntentInfo> GetScheduledIntentsSnapshot()
        {
            return _events.Select(e => new ScheduledIntentInfo
            {
                Id = e.Id,
                GroupId = e.GroupId,
                Time = e.Tick * SecondsPerTick, // ��ʾʱת����
                Owner = e.Intent?.Owner,
                Type = e.Intent?.Type ?? ActionType.None,
                Description = e.Description
            }).ToList();
        }

        public List<ScheduledIntentDetailedInfo> GetScheduledIntentsDetailedSnapshot()
        {
            return _events.Select(e => new ScheduledIntentDetailedInfo
            {
                Id = e.Id,
                GroupId = e.GroupId,
                Tick = e.Tick,
                Time = e.Tick * SecondsPerTick,
                Priority = e.Priority,
                Intent = e.Intent,
                Description = e.Description
            }).ToList();
        }
    }
}
