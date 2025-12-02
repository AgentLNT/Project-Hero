using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProjectHero.Core.Timeline
{
    public class BattleTimeline : MonoBehaviour
    {
        public struct TimelineEvent
        {
            public float Time;
            public string Description;
            public System.Action Action;
            public int Priority; // Higher executes first at same time
        }

        private List<TimelineEvent> _events = new List<TimelineEvent>();
        public float CurrentTime { get; private set; } = 0f;

        public void ScheduleEvent(float delay, string description, System.Action action, int priority = 0)
        {
            _events.Add(new TimelineEvent 
            { 
                Time = CurrentTime + delay, 
                Description = description, 
                Action = action,
                Priority = priority
            });
            SortEvents();
        }

        public void InsertReaction(float absoluteTime, string description, System.Action action)
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
                Priority = 10 // High priority for reactions
            });
            SortEvents();
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
