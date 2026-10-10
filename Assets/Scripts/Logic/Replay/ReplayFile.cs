using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;

namespace ProjectHero.Logic.Replay
{
    /// <summary>Versioned developer file format. Closed command tags; never resolves runtime types from files.</summary>
    public static class ReplayFile
    {
        private const string Magic = "ProjectHero.DeveloperReplay.v4";
        private const int MaximumRecords = 2000000;
        public static void Save(Stream stream, BattleReplay replay)
        {
            if (replay?.Header == null || replay.Header.ReplayFormatVersion != Determinism.ReplayFormat.Version)
                throw new LogicDefinitionException(ReplayCodes.REPLAY_FORMAT_VERSION_MISMATCH, "");
            foreach (var record in replay.Records)
                if (record.ReplayInputRequests != null && record.ReplayInputRequests.Count != 0)
                    throw new LogicDefinitionException("REPLAY_SUBMISSION_BOUNDARY_MISSING", "");
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                ReplayHeader h = replay.Header;
                writer.Write(h.ReplayFormatVersion); writer.Write(h.RulesVersion); writer.Write(h.BattleDefinitionHash);
                writer.Write(h.TicksPerSecond); writer.Write(h.EncounterId.Value);
                writer.Write(h.RuntimeInputs.InitialRngSeed); writer.Write(h.RuntimeInputs.InitialMetaResource); writer.Write(h.InitialStateHash);
                writer.Write(replay.Submissions.Count);
                foreach (var s in replay.Submissions)
                {
                    writer.Write(s.SubmittedAtTick); writer.Write(s.Fact.Issuer.Value);
                    writer.Write((int)s.Fact.SourceKind); writer.Write(s.Fact.ProducerOrdinal);
                    WriteRequest(writer, s.Fact.Request);
                }
                writer.Write(replay.Records.Count);
                foreach (var record in replay.Records)
                {
                    writer.Write(record.Tick); writer.Write(record.SnapshotHash); writer.Write(record.Events.Count);
                    foreach (var fact in record.Events)
                    { writer.Write(fact.Tick); writer.Write(fact.Sequence); writer.Write(ReplayEventComparison.Canonical(fact)); }
                }
            }
        }

        public static BattleReplay Load(Stream stream, BattleDefinition definition)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadString() != Magic) throw new LogicDefinitionException(ReplayCodes.REPLAY_FORMAT_VERSION_MISMATCH, "file magic");
                var header = new ReplayHeader(reader.ReadInt32(), reader.ReadString(), reader.ReadString(), reader.ReadInt32(),
                    new EncounterDefinitionId(reader.ReadString()), new BattleRuntimeInputs(reader.ReadUInt64(), reader.ReadInt32()), reader.ReadUInt64());
                string error = ReplayHeaderValidation.Validate(header, definition);
                if (error != null) throw new LogicDefinitionException(error, "");
                int count = ReadCount(reader);
                var submissions = new ReplaySubmission[count];
                for (int i = 0; i < count; i++)
                {
                    long boundary = reader.ReadInt64();
                    var issuer = new ControllerId(reader.ReadString());
                    var source = (CommandSourceKind)reader.ReadInt32(); long ordinal = reader.ReadInt64();
                    submissions[i] = new ReplaySubmission(boundary, new RecordedCommandRequest(issuer, source, ordinal, ReadRequest(reader)));
                }
                count = ReadCount(reader);
                var records = new ReplayTickRecord[count];
                for (int i = 0; i < count; i++)
                {
                    long tick = reader.ReadInt64(); ulong hash = reader.ReadUInt64();
                    var events = new LogicEvent[ReadCount(reader)];
                    for (int j = 0; j < events.Length; j++)
                        events[j] = new StoredReplayEvent(reader.ReadInt64(), reader.ReadInt64(), reader.ReadString());
                    records[i] = new ReplayTickRecord(tick, Array.Empty<RecordedCommandRequest>(), Array.AsReadOnly(events), hash);
                }
                if (stream.CanSeek && stream.Position != stream.Length) throw new LogicDefinitionException("REPLAY_TRAILING_DATA", "");
                return new BattleReplay(header, records, submissions);
            }
        }

        private static int ReadCount(BinaryReader reader)
        {
            int n = reader.ReadInt32();
            if (n < 0 || n > MaximumRecords) throw new LogicDefinitionException("REPLAY_COLLECTION_SIZE_INVALID", "");
            return n;
        }

        internal static CommandRequest CopyRequest(CommandRequest request)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) WriteRequest(writer, request);
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) return ReadRequest(reader);
            }
        }

        private static void WriteRequest(BinaryWriter w, CommandRequest request)
        {
            w.Write(request.TargetTick);
            switch (request.Scope)
            {
                case ScheduleEditScope s: w.Write(0); w.Write(s.ExpectedScheduleRevision); WriteOptional(w, s.ExpectedWindowId?.Value); break;
                case WindowCommandScope s: w.Write(1); w.Write(s.ExpectedWindowId.Value); break;
                case ReactionCommandScope s: w.Write(2); w.Write(s.ReactionOpportunityId.Value); break;
                default: throw new LogicDefinitionException("REPLAY_SCOPE_UNSUPPORTED", "");
            }
            switch (request.Payload)
            {
                case ScheduleEditPayload edit:
                    w.Write(0); w.Write(edit.Operations.Count);
                    foreach (var operation in edit.Operations) WriteOperation(w, operation);
                    break;
                case WindowCommandPayload window: w.Write(1); w.Write((int)window.WindowKind); break;
                case ReactionCommandPayload reaction:
                    w.Write(2); w.Write((int)reaction.ReactionKind); w.Write(reaction.ReactionActionSpecId.Value); WritePoint(w, reaction.DodgeDestination); break;
                default: throw new LogicDefinitionException("REPLAY_PAYLOAD_UNSUPPORTED", "");
            }
        }

        private static CommandRequest ReadRequest(BinaryReader r)
        {
            long target = r.ReadInt64();
            CommandScope scope;
            switch (r.ReadInt32())
            {
                case 0: long revision = r.ReadInt64(); long? window = ReadOptional(r); scope = new ScheduleEditScope(revision, window.HasValue ? new WindowId(window.Value) : (WindowId?)null); break;
                case 1: scope = new WindowCommandScope(new WindowId(r.ReadInt64())); break;
                case 2: scope = new ReactionCommandScope(new ReactionOpportunityId(r.ReadInt64())); break;
                default: throw new LogicDefinitionException("REPLAY_SCOPE_UNSUPPORTED", "");
            }
            ICommandPayload payload;
            switch (r.ReadInt32())
            {
                case 0:
                    var operations = new ScheduleEditOperation[ReadCount(r)];
                    for (int i = 0; i < operations.Length; i++) operations[i] = ReadOperation(r);
                    payload = new ScheduleEditPayload(Array.AsReadOnly(operations)); break;
                case 1: payload = new WindowCommandPayload((WindowCommandKind)r.ReadInt32()); break;
                case 2: payload = new ReactionCommandPayload((ReactionCommandKind)r.ReadInt32(), new ActionSpecId(r.ReadString()), ReadPoint(r)); break;
                default: throw new LogicDefinitionException("REPLAY_PAYLOAD_UNSUPPORTED", "");
            }
            return new CommandRequest(target, scope, payload);
        }

        private static void WriteOperation(BinaryWriter w, ScheduleEditOperation operation)
        {
            switch (operation)
            {
                case AddOrdinaryPlanOperation a:
                    w.Write(0); w.Write(a.TemporaryPlanKey); w.Write(a.OwnerUnitId.Value); w.Write(a.ActionSpecId.Value);
                    WriteOptional(w, a.RequestedStartTick); w.Write(a.AnchorAfterPlanId.Value);
                    WriteOptional(w, a.PrimaryTargetUnitId?.Value); w.Write((int)a.Facing); WritePoint(w, a.Destination); break;
                case MoveEditablePlanOperation m: w.Write(1); w.Write(m.PlanId.Value); w.Write(m.RequestedStartTick); w.Write(m.AnchorAfterPlanId.Value); break;
                case RemoveEditablePlanOperation d: w.Write(2); w.Write(d.PlanId.Value); w.Write((int)d.TerminationReason); break;
                // Retain task03 compatibility commands too; they may be validly recorded rule rejections.
                case ScheduleAddOperation a: w.Write(3); w.Write(a.PlanId.Value); w.Write(a.RequestedStartTick); break;
                case ScheduleMoveOperation m: w.Write(4); w.Write(m.PlanId.Value); w.Write(m.RequestedStartTick); break;
                case ScheduleRemoveOperation d: w.Write(5); w.Write(d.PlanId.Value); break;
                default: throw new LogicDefinitionException("REPLAY_OPERATION_UNSUPPORTED", "");
            }
        }

        private static ScheduleEditOperation ReadOperation(BinaryReader r)
        {
            switch (r.ReadInt32())
            {
                case 0:
                    long key = r.ReadInt64(); var unit = new UnitId(r.ReadInt64()); var action = new ActionSpecId(r.ReadString());
                    long? start = ReadOptional(r); long anchorValue = r.ReadInt64(); var anchor = anchorValue == 0 ? default : new ActionPlanId(anchorValue);
                    long? target = ReadOptional(r); var facing = (GridDirection)r.ReadInt32(); var point = ReadPoint(r);
                    return new AddOrdinaryPlanOperation(key, unit, action, start, anchor, target.HasValue ? new UnitId(target.Value) : (UnitId?)null, facing, point);
                case 1:
                    var id = new ActionPlanId(r.ReadInt64()); long tick = r.ReadInt64(); long a = r.ReadInt64();
                    return new MoveEditablePlanOperation(id, tick, a == 0 ? default : new ActionPlanId(a));
                case 2: return new RemoveEditablePlanOperation(new ActionPlanId(r.ReadInt64()), (ActionTerminationReason)r.ReadInt32());
                case 3: return new ScheduleAddOperation(new ActionPlanId(r.ReadInt64()), r.ReadInt64());
                case 4: return new ScheduleMoveOperation(new ActionPlanId(r.ReadInt64()), r.ReadInt64());
                case 5: return new ScheduleRemoveOperation(new ActionPlanId(r.ReadInt64()));
                default: throw new LogicDefinitionException("REPLAY_OPERATION_UNSUPPORTED", "");
            }
        }
        private static void WriteOptional(BinaryWriter w, long? value) { w.Write(value.HasValue); if (value.HasValue) w.Write(value.Value); }
        private static long? ReadOptional(BinaryReader r) => r.ReadBoolean() ? r.ReadInt64() : (long?)null;
        private static void WritePoint(BinaryWriter w, GridPoint? point)
        { w.Write(point.HasValue); if (point.HasValue) { w.Write(point.Value.X); w.Write(point.Value.Y); } }
        private static GridPoint? ReadPoint(BinaryReader r) => r.ReadBoolean() ? new GridPoint(r.ReadInt32(), r.ReadInt32()) : (GridPoint?)null;
    }
}
