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
            public float Time;
            public CombatIntent Intent;
            public string Description;
        }

        private List<ScheduledIntent> _events = new List<ScheduledIntent>();
        private List<CombatIntent> _pendingIntents = new List<CombatIntent>(); // Intents for the current frame

        public float CurrentTime { get; private set; } = 0f;

        /// <summary>
        /// Schedules an intent to happen after a specific delay relative to CurrentTime.
        /// </summary>
        public void Schedule(float delay, CombatIntent intent, string description = null)
        {
            _events.Add(new ScheduledIntent
            {
                Time = CurrentTime + delay,
                Intent = intent,
                Description = description ?? intent.ToString()
            });
            
            // Keep sorted by time
            _events.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        /// <summary>
        /// Cancels all future intents for a specific unit.
        /// </summary>
        public void CancelEvents(CombatUnit unit)
        {
            int removed = _events.RemoveAll(e => e.Intent.Owner == unit);
            if (removed > 0)
            {
                Debug.Log($"[Timeline] Cancelled {removed} events for {unit.name}");
            }
        }

        public void AdvanceTime(float newTime)
        {
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
