using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ProjectHero.Core.Entities;

namespace ProjectHero.Core.Timeline
{
    public class BattleTimeline : MonoBehaviour
    {
        public struct TimelineEvent
        {
            public float Time;
            public string Description;
            public System.Action Action;
            public CombatUnit Owner; // The unit performing the action (can be null)
            public int Priority; // Higher executes first at same time
        }

        private List<TimelineEvent> _events = new List<TimelineEvent>();
        public float CurrentTime { get; private set; } = 0f;

        public void ScheduleEvent(float delay, string description, System.Action action, CombatUnit owner = null, int priority = 0)
        {
            _events.Add(new TimelineEvent 
            { 
                Time = CurrentTime + delay, 
                Description = description, 
                Action = action,
                Owner = owner,
                Priority = priority
            });
            SortEvents();
        }

        public void InsertReaction(float absoluteTime, string description, System.Action action, CombatUnit owner = null)
        {
            // Insert an event at a specific absolute time (e.g. for blocks/dodges)
            if (absoluteTime < CurrentTime)
            {
                Debug.LogWarning("Cannot insert event in the past!");
                return;
            }
            
            _events.Add(new TimelineEvent
            {
                Time = absoluteTime,
                Description = description,
                Action = action,
                Owner = owner,
                Priority = 10 // High priority for reactions
            });
            SortEvents();
        }

        /// <summary>
        /// Cancels all pending events for a specific unit.
        /// Useful when a unit is Staggered, Knocked Down, or Killed.
        /// </summary>
        public void CancelEvents(CombatUnit unit)
        {
            int removedCount = _events.RemoveAll(e => e.Owner == unit);
            if (removedCount > 0)
            {
                Debug.Log($"[Timeline] Cancelled {removedCount} events for {unit.name}");
            }
        }

        private void SortEvents()
        {
            _events = _events.OrderBy(e => e.Time).ThenByDescending(e => e.Priority).ToList();
        }

        public void AdvanceTime()
        {
            if (_events.Count == 0) return;

            var nextEvent = _events[0];
            _events.RemoveAt(0);

            CurrentTime = nextEvent.Time;
            Debug.Log($"[T={CurrentTime:F2}] Executing: {nextEvent.Description}");
            nextEvent.Action?.Invoke();
        }

        [ContextMenu("Preview Next Event")]
        public void PeekNext()
        {
            if (_events.Count > 0)
            {
                Debug.Log($"Next Event: {_events[0].Description} at T={_events[0].Time:F2}");
            }
        }
    }
}
