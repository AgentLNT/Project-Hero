using System.Collections.Generic;
using ProjectHero.Authoring.Legacy;
using System.Linq;
using UnityEngine;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;

namespace ProjectHero.Core.Timeline
{
    /// <summary>
    /// 旧离散时间线。
    ///
    /// 任务 03B 的所有权调整（不变量 21）：本类<strong>没有</strong> <c>Update()</c>，
    /// <c>AdvanceTime(float)</c> 唯一的调用者是 <c>BattleRuntimeBootstrap</c> 经 Legacy 帧适配器
    /// （<c>CombatDemo.AdvanceFrame</c>）发起的那一条路径。时间语义完全不变：
    /// 受 <c>Time.timeScale</c> 缩放的帧时间 + 固定步长累加器 + 暂停即 return。
    /// </summary>
    [DefaultExecutionOrder(ProjectHero.Core.Compatibility.Runtime.RuntimeCallbackRegistry.LegacyWriterExecutionOrder)]
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

        /// <summary>
        /// 用户暂停（P 键 / UI 的 <see cref="SetPaused"/>）。与 <see cref="SystemPaused"/> 是
        /// <strong>两个独立来源</strong>：系统暂停由战斗生命周期施加（<c>StopBattle</c> 置位、
        /// <c>ResetForNewBattle</c> 清除），用户暂停是玩家意图，战斗之间的重开<strong>不得</strong>
        /// 清除它（恢复语义见 <c>CombatDemo.ResetForNewBattle</c>）。
        ///
        /// 只读可观察入口：第三收尾轮 R4 之前只有私有字段 <c>_userPaused</c>，
        /// "用户暂停不被重开清除"这条语义<strong>没有</strong>可断言入口；
        /// 本属性只暴露事实，不改变任何暂停语义（<see cref="SetPaused"/> 仍是唯一写入点）。
        /// </summary>
        public bool UserPaused => _userPaused;

        /// <summary>
        /// 旧时间线里当前<strong>尚未执行</strong>的排程条目数（只读可观察入口）。
        ///
        /// 用途：让 Shadow 比较能<strong>真读旧运行的排程事实</strong>，而不是用定义槽位等
        /// 定义派生常量冒充旧侧事实（03B-交接记录 §23.2 的任务 04 前置条件）。
        /// 它只暴露计数，不暴露条目内容，也不新增任何写入面：<c>Schedule</c> /
        /// <c>CancelGroup</c> / <c>CancelEvents</c> 仍是唯一写入点。
        ///
        /// <strong>语义边界</strong>：旧排程表混合了动作 Intent 与状态改变 Intent，它不是
        /// 新内核 <c>StatusEffect</c> 或 <c>ActionPlan</c> 的对应物，因此本计数<strong>不</strong>参与
        /// 两侧必须相等的字段级比较；它只用于旧侧事实确实被读过的可证伪负控制。
        /// </summary>
        public int ScheduledEventCount => _events.Count;

        /// <summary>
        /// 旧顶层推进的累计调用次数与累计帧时间（任务 03B「必须产出」9 的权威计数来源）。
        ///
        /// 这两个值只<strong>记录事实</strong>：不参与暂停判断、不改变累加器、不影响 Tick 推进。
        /// 它们让"New 模式旧 <c>AdvanceTime</c> 调用为 0"可以被直接断言，
        /// 而不是靠日志文本推断。
        /// </summary>
        public int TotalAdvanceTimeCalls { get; private set; }

        /// <summary>累计收到的帧时间（含暂停期间收到的部分）。</summary>
        public float RecordedAdvanceTimeSeconds { get; private set; }

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
            TotalAdvanceTimeCalls++;
            RecordedAdvanceTimeSeconds += deltaTimeReal;

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
