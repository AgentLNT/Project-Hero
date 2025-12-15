using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;

namespace ProjectHero.Core.Timeline
{
    /// <summary>
    /// Pure Scheduler. Manages a list of CombatIntents and executes them at the right time.
    /// Delegates conflict resolution to CombatArbiter.
    /// </summary>
    public class BattleTimeline : MonoBehaviour
    {
        private struct ScheduledIntent
        {
            public long Id;
            public long GroupId;
            public float Time;
            public CombatIntent Intent;
            public string Description;
        }

        private List<ScheduledIntent> _events = new List<ScheduledIntent>();
        private List<CombatIntent> _pendingIntents = new List<CombatIntent>(); // Intents for the current frame

        private long _nextEventId = 1;
        private long _nextGroupId = 1;

        public float CurrentTime { get; private set; } = 0f;
        public bool Paused { get; private set; } = false;

        public void SetPaused(bool paused)
        {
            Paused = paused;
        }

        public struct ScheduledIntentInfo
        {
            public long Id;
            public long GroupId;
            public float Time;
            public CombatUnit Owner;
            public ActionType Type;
            public string Description;
        }

        public long ReserveGroupId()
        {
            return _nextGroupId++;
        }

        public List<ScheduledIntentInfo> GetScheduledIntentsSnapshot()
        {
            return _events
                .Select(e => new ScheduledIntentInfo
                {
                    Id = e.Id,
                    GroupId = e.GroupId,
                    Time = e.Time,
                    Owner = e.Intent != null ? e.Intent.Owner : null,
                    Type = e.Intent != null ? e.Intent.Type : ActionType.None,
                    Description = e.Description
                })
                .ToList();
        }

        /// <summary>
        /// Schedules an intent to happen after a specific delay relative to CurrentTime.
        /// </summary>
        public long Schedule(float delay, CombatIntent intent, string description = null, long groupId = 0)
        {
            long id = _nextEventId++;
            _events.Add(new ScheduledIntent
            {
                Id = id,
                GroupId = groupId,
                Time = CurrentTime + delay,
                Intent = intent,
                Description = description ?? intent.ToString()
            });
            
            // Keep sorted by time
            // NOTE: Some events are scheduled extremely close together (e.g., Move step boundary vs delayed commit).
            // When absolute time values become equal/near-equal due to float resolution, execution order must still be stable.
            const float tieWindowSeconds = 0.001f;
            _events.Sort((a, b) =>
            {
                float dt = a.Time - b.Time;
                if (dt < -tieWindowSeconds) return -1;
                if (dt > tieWindowSeconds) return 1;

                int pa = GetSortPriority(a.Intent);
                int pb = GetSortPriority(b.Intent);
                int p = pa.CompareTo(pb);
                if (p != 0) return p;

                return a.Id.CompareTo(b.Id);
            });

            return id;
        }

        private static int GetSortPriority(CombatIntent intent)
        {
            // Lower is earlier.
            // Commit intents must execute before any same-timestamp actions that depend on the committed state.
            if (intent is ProjectHero.Core.Actions.Intents.CommitMoveStepIntent) return 0;
            return 10;
        }

        /// <summary>
        /// Schedules an intent to happen at an absolute time.
        /// </summary>
        public long ScheduleAt(float absoluteTime, CombatIntent intent, string description = null, long groupId = 0)
        {
            float delay = absoluteTime - CurrentTime;
            if (delay < 0f) delay = 0f;
            return Schedule(delay, intent, description, groupId);
        }

        public void CancelEvent(long eventId)
        {
            _events.RemoveAll(e => e.Id == eventId);
        }

        public void CancelGroup(long groupId)
        {
            if (groupId == 0) return;
            // Release any movement reservations tied to commit intents we are about to remove.
            for (int i = 0; i < _events.Count; i++)
            {
                var evt = _events[i];
                if (evt.GroupId != groupId) continue;

                if (evt.Intent is ProjectHero.Core.Actions.Intents.CommitMoveStepIntent commit)
                {
                    commit.ReleaseReservation();
                }
            }

            int removed = _events.RemoveAll(e => e.GroupId == groupId);
            if (removed > 0)
            {
                Debug.Log($"[Timeline] Cancelled {removed} events for Group {groupId}");
            }
        }

        /// <summary>
        /// Cancels all future intents for a specific unit.
        /// </summary>
        public void CancelEvents(CombatUnit unit)
        {
            // Release any movement reservations tied to commit intents we are about to remove.
            for (int i = 0; i < _events.Count; i++)
            {
                var evt = _events[i];
                if (evt.Intent == null || evt.Intent.Owner != unit) continue;

                if (evt.Intent is ProjectHero.Core.Actions.Intents.CommitMoveStepIntent commit)
                {
                    commit.ReleaseReservation();
                }
            }

            int removed = _events.RemoveAll(e => e.Intent.Owner == unit);
            if (removed > 0)
            {
                Debug.Log($"[Timeline] Cancelled {removed} events for {unit.name}");
            }
        }

        public void AdvanceTime(float newTime)
        {
            if (Paused)
            {
                _pendingIntents.Clear();
                return;
            }

            CurrentTime = newTime;
            _pendingIntents.Clear();

            // 1. Collect all intents due for this frame
            while (_events.Count > 0 && _events[0].Time <= CurrentTime)
            {
                var evt = _events[0];
                _events.RemoveAt(0);
                
                Debug.Log($"[T={evt.Time:F2}] Processing: {evt.Description}");
                _pendingIntents.Add(evt.Intent);
            }

            // 2. Resolve Conflicts (Arbiter)
            if (_pendingIntents.Count > 0)
            {
                CombatArbiter.Resolve(_pendingIntents);
            }
        }
    }
}
