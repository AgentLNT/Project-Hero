using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Interactions; // Added

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
            public bool IsForced; // If true, this event cannot be cancelled by normal interruptions (e.g. Knockback)
            public ActionType Tag; // Added for Interaction System
            public object Data; // Generic data payload (e.g. the Action object)
        }

        private List<TimelineEvent> _events = new List<TimelineEvent>();
        // Store events processed in the current frame/step to allow mutual visibility (Clash logic)
        private List<TimelineEvent> _processedEvents = new List<TimelineEvent>(); 
        
        // New: List of intents generated in the current frame
        private List<CombatIntent> _pendingIntents = new List<CombatIntent>();

        public float CurrentTime { get; private set; } = 0f;

        public void RegisterIntent(CombatIntent intent)
        {
            _pendingIntents.Add(intent);
        }

        public void ScheduleEvent(float delay, string description, System.Action action, CombatUnit owner = null, int priority = 0, bool isForced = false, ActionType tag = ActionType.None, object data = null)
        {
            _events.Add(new TimelineEvent 
            { 
                Time = CurrentTime + delay, 
                Description = description, 
                Action = action,
                Owner = owner,
                Priority = priority,
                IsForced = isForced,
                Tag = tag,
                Data = data
            });
            SortEvents();
        }

        /// <summary>
        /// Queries events for a specific unit within a time range.
        /// Checks both pending events AND events already processed in this time step.
        /// </summary>
        public List<TimelineEvent> GetEvents(CombatUnit owner, float startTime, float endTime)
        {
            var pending = _events.Where(e => e.Owner == owner && e.Time >= startTime && e.Time <= endTime);
            var processed = _processedEvents.Where(e => e.Owner == owner && e.Time >= startTime && e.Time <= endTime);
            
            return pending.Concat(processed).ToList();
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
        public void CancelEvents(CombatUnit unit, bool includeForced = false)
        {
            int removedCount = _events.RemoveAll(e => e.Owner == unit && (includeForced || !e.IsForced));
            if (removedCount > 0)
            {
                Debug.Log($"[Timeline] Cancelled {removedCount} events for {unit.name} (IncludeForced: {includeForced})");
            }
        }

        private void SortEvents()
        {
            _events = _events.OrderBy(e => e.Time).ThenByDescending(e => e.Priority).ToList();
        }

        public void AdvanceTime(float timer)
        {
            CurrentTime = timer; 
            _processedEvents.Clear(); // Clear history from previous frames

            // 1. Collect all events that should run now
            // We move them to _processedEvents so they are visible to GetEvents() during execution
            while (_events.Count > 0 && _events[0].Time <= CurrentTime)
            {
                var nextEvent = _events[0];
                _events.RemoveAt(0);
                _processedEvents.Add(nextEvent);
            }

            // 2. Execute them
            // Note: We iterate over a copy or index because execution might add new events (though usually for future)
            // If an event adds a new event at CurrentTime, it goes to _events.
            // We should probably loop until _events is empty for CurrentTime.
            
            // Execute the batch we just collected
            foreach (var evt in _processedEvents)
            {
                Debug.Log($"[T={evt.Time:F2}] Executing: {evt.Description}");
                evt.Action?.Invoke();
            }
            
            // Handle immediate follow-up events (if any were added during execution for the SAME time)
            while (_events.Count > 0 && _events[0].Time <= CurrentTime)
            {
                var nextEvent = _events[0];
                _events.RemoveAt(0);
                _processedEvents.Add(nextEvent); // Add to history
                
                Debug.Log($"[T={nextEvent.Time:F2}] Executing (Immediate): {nextEvent.Description}");
                nextEvent.Action?.Invoke();
            }

            // 3. Resolve Interactions (New Architecture)
            // After all events have fired and registered their intents, we let the Arbiter decide the outcome.
            if (_pendingIntents.Count > 0)
            {
                Debug.Log($"[Timeline] Resolving {_pendingIntents.Count} intents via Arbiter...");
                CombatArbiter.Resolve(_pendingIntents);
                _pendingIntents.Clear();
            }
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
