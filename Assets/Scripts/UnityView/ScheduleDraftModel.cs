using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Commands;

namespace ProjectHero.UnityView
{
    public enum ScheduleDraftState { Editing, AwaitingResume, AwaitingAcceptance, Accepted, Rejected, Stale }
    public sealed class ScheduleDraftEntry
    {
        public long Id { get; } public ScheduleEditOperation Operation { get; }
        public ScheduleDraftEntry(long id, ScheduleEditOperation operation) { Id = id; Operation = operation; }
    }
    /// <summary>Stable local IDs, independent of authoritative plan IDs. One queue per player view.</summary>
    public sealed class ScheduleDraftModel
    {
        private readonly List<ScheduleDraftEntry> _entries = new List<ScheduleDraftEntry>();
        private long _nextId = 1;
        public IReadOnlyList<ScheduleDraftEntry> Entries => _entries.AsReadOnly();
        public IReadOnlyList<ScheduleEditOperation> Operations => _entries.Select(e => e.Operation).ToArray();
        public ScheduleDraftState State { get; internal set; } = ScheduleDraftState.Editing;
        public long BaseRevision { get; internal set; }
        public long BaseWindowId { get; internal set; }
        public long BaseTick { get; internal set; }
        public string Reason { get; internal set; }
        public bool HasIntent => _entries.Count > 0;
        public long Append(ScheduleEditOperation operation)
        {
            if (State == ScheduleDraftState.AwaitingAcceptance) return 0;
            long id = _nextId++;
            if (operation is AddOrdinaryPlanOperation add) operation = add with { TemporaryPlanKey = id };
            _entries.Add(new ScheduleDraftEntry(id, operation)); State = ScheduleDraftState.Editing; return id;
        }
        public bool Replace(long id, ScheduleEditOperation operation)
        {
            if (State == ScheduleDraftState.AwaitingAcceptance) return false;
            int index = _entries.FindIndex(e => e.Id == id); if (index < 0) return false;
            if (operation is AddOrdinaryPlanOperation add) operation = add with { TemporaryPlanKey = id };
            _entries[index] = new ScheduleDraftEntry(id, operation); State = ScheduleDraftState.Editing; return true;
        }
        public bool Remove(long id)
        {
            if (State == ScheduleDraftState.AwaitingAcceptance) return false;
            bool removed = _entries.RemoveAll(e => e.Id == id) > 0; if (removed) State = ScheduleDraftState.Editing; return removed;
        }
        public void Clear() { _entries.Clear(); State = ScheduleDraftState.Editing; Reason = null; }
        public void Reset() { Clear(); _nextId = 1; BaseRevision = BaseWindowId = 0; BaseTick = -1; }
    }
}
