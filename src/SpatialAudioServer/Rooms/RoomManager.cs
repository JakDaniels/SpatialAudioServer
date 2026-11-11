using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Media;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Rooms;

/// <summary>
/// Owns the live rooms and participants. Room definitions are persisted; participants live only in memory
/// (clients rejoin after a restart). All state changes take one lock, keep SQLite in step inside it, and notify
/// the media layer after it is released.
/// </summary>
public sealed class RoomManager(
    RoomStore rooms,
    JoinTokenStore joinTokens,
    ActivityLog activity,
    IRoomMedia media,
    IOptions<LimitOptions> limits,
    IOptions<AuthOptions> auth,
    TimeProvider time,
    ILogger<RoomManager> log)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Room> _rooms = [];
    private bool _restored;

    public int RoomCount
    {
        get
        {
            lock (_lock)
            {
                return _rooms.Count;
            }
        }
    }

    public int ParticipantCount
    {
        get
        {
            lock (_lock)
            {
                return _rooms.Values.Sum(r => r.Participants.Count);
            }
        }
    }

    /// <summary>
    /// Loads persisted rooms (empty) and clears what a previous run left behind: open sessions and join tokens.
    /// </summary>
    public void Restore()
    {
        List<RoomSnapshot> restored;
        lock (_lock)
        {
            if (_restored)
            {
                return;
            }
            _restored = true;

            var closed = activity.CloseOpenSessions("server-restart");
            joinTokens.DeleteAll();
            foreach (var record in rooms.All())
            {
                _rooms[record.Id] = new Room(record);
            }
            restored = _rooms.Values.Select(r => r.Snapshot()).ToList();
            log.LogInformation("Restored {Rooms} rooms; closed {Sessions} sessions left open by the previous run",
                restored.Count, closed);
        }
        restored.ForEach(media.RoomCreated);
    }

    public RoomSnapshot CreateRoom(CreateRoomCommand command)
    {
        var limit = limits.Value;
        new Validation()
            .Required("name", command.Name, Validation.MaxNameLength)
            .Optional("externalId", command.ExternalId, Validation.MaxExternalIdLength)
            .Range("maxParticipants", command.MaxParticipants, 1, limit.MaxParticipantsPerRoom)
            .Metadata("metadata", command.Metadata)
            .ThrowIfInvalid();

        RoomSnapshot snapshot;
        lock (_lock)
        {
            if (_rooms.Count >= limit.MaxRooms)
            {
                throw new LimitExceededException($"The server already has the maximum of {limit.MaxRooms} rooms.");
            }
            var record = new RoomRecord(
                Guid.NewGuid(), command.Name!.Trim(), command.ExternalId, command.MaxParticipants ?? limit.MaxParticipantsPerRoom,
                command.Metadata?.ToJsonString(), time.GetUtcNow());
            rooms.Insert(record);
            var room = new Room(record);
            _rooms[room.Id] = room;
            snapshot = room.Snapshot();
            activity.Event("room.created", room.Id, detail: room.Name);
        }
        log.LogInformation("Room {RoomId} '{Name}' created", snapshot.Id, snapshot.Name);
        media.RoomCreated(snapshot);
        return snapshot;
    }

    public IReadOnlyList<RoomSnapshot> ListRooms(string? externalId = null)
    {
        lock (_lock)
        {
            return _rooms.Values
                .Where(r => externalId is null || r.ExternalId == externalId)
                .OrderBy(r => r.CreatedAt)
                .Select(r => r.Snapshot())
                .ToList();
        }
    }

    public (RoomSnapshot Room, IReadOnlyList<ParticipantSnapshot> Participants) GetRoom(Guid roomId)
    {
        lock (_lock)
        {
            var room = FindRoom(roomId);
            return (room.Snapshot(), room.ParticipantSnapshots());
        }
    }

    /// <summary>Deletes the room and disconnects everyone in it.</summary>
    public void DeleteRoom(Guid roomId)
    {
        List<Guid> removed;
        lock (_lock)
        {
            var room = FindRoom(roomId);
            removed = room.Participants.Keys.ToList();
            foreach (var pid in removed)
            {
                EndParticipant(room, pid, "room-deleted");
            }
            rooms.Delete(roomId);
            _rooms.Remove(roomId);
            activity.Event("room.deleted", roomId, detail: room.Name);
        }
        log.LogInformation("Room {RoomId} deleted ({Count} participants removed)", roomId, removed.Count);
        foreach (var pid in removed)
        {
            media.ParticipantRemoved(roomId, pid);
        }
        media.RoomDeleted(roomId);
    }

    public JoinResult AddParticipant(Guid roomId, AddParticipantCommand command)
    {
        new Validation()
            .Required("displayName", command.DisplayName, Validation.MaxNameLength)
            .Optional("externalId", command.ExternalId, Validation.MaxExternalIdLength)
            .Position("position", command.Position)
            .Orientation("orientation", command.Orientation)
            .Metadata("metadata", command.Metadata)
            .ThrowIfInvalid();

        JoinResult result;
        lock (_lock)
        {
            var room = FindRoom(roomId);
            if (room.Participants.Count >= room.MaxParticipants)
            {
                throw new LimitExceededException($"Room {roomId} is full ({room.MaxParticipants} participants).");
            }
            var total = _rooms.Values.Sum(r => r.Participants.Count);
            if (total >= limits.Value.MaxParticipantsTotal)
            {
                throw new LimitExceededException(
                    $"The server already has the maximum of {limits.Value.MaxParticipantsTotal} participants.");
            }

            var participant = new Participant
            {
                Id = Guid.NewGuid(),
                RoomId = roomId,
                DisplayName = command.DisplayName!.Trim(),
                ExternalId = command.ExternalId,
                Position = command.Position,
                Orientation = command.Orientation,
                Metadata = command.Metadata?.ToJsonString(),
                JoinedAt = time.GetUtcNow(),
            };
            var (token, expires) = joinTokens.Issue(participant.Id, roomId, auth.Value.JoinTokenLifetime);
            activity.SessionStarted(participant.Id, roomId, participant.ExternalId, participant.DisplayName, participant.JoinedAt);
            activity.Event("participant.joined", roomId, participant.Id, participant.DisplayName);
            room.Participants[participant.Id] = participant;
            result = new JoinResult(participant.Snapshot(), token, expires);
        }
        log.LogInformation("Participant {ParticipantId} '{Name}' added to room {RoomId}",
            result.Participant.Id, result.Participant.DisplayName, roomId);
        media.ParticipantAdded(result.Participant);
        return result;
    }

    public IReadOnlyList<ParticipantSnapshot> ListParticipants(Guid roomId)
    {
        lock (_lock)
        {
            return FindRoom(roomId).ParticipantSnapshots();
        }
    }

    public ParticipantSnapshot GetParticipant(Guid roomId, Guid participantId)
    {
        lock (_lock)
        {
            return FindParticipant(FindRoom(roomId), participantId).Snapshot();
        }
    }

    public void RemoveParticipant(Guid roomId, Guid participantId, string reason = "removed")
    {
        lock (_lock)
        {
            var room = FindRoom(roomId);
            FindParticipant(room, participantId);
            EndParticipant(room, participantId, reason);
        }
        log.LogInformation("Participant {ParticipantId} removed from room {RoomId} ({Reason})", participantId, roomId, reason);
        media.ParticipantRemoved(roomId, participantId);
    }

    public ParticipantSnapshot UpdateParticipant(Guid roomId, Guid participantId, UpdateParticipantCommand command)
    {
        new Validation()
            .Range("gain", command.Gain, 0, Validation.MaxGain)
            .Position("position", command.Position)
            .Orientation("orientation", command.Orientation)
            .ThrowIfInvalid();

        ParticipantSnapshot snapshot;
        lock (_lock)
        {
            var participant = FindParticipant(FindRoom(roomId), participantId);
            if (command.Muted is { } muted && muted != participant.Muted)
            {
                participant.Muted = muted;
                // Mute changes are worth an audit entry; position updates (frequent, from the region module) are not.
                activity.Event(muted ? "participant.muted" : "participant.unmuted", roomId, participantId);
            }
            participant.Gain = command.Gain ?? participant.Gain;
            participant.Position = command.Position ?? participant.Position;
            participant.Orientation = command.Orientation ?? participant.Orientation;
            snapshot = participant.Snapshot();
        }
        media.ParticipantUpdated(snapshot);
        return snapshot;
    }

    private Room FindRoom(Guid roomId) =>
        _rooms.TryGetValue(roomId, out var room) ? room : throw new NotFoundException($"Room {roomId} not found.");

    private static Participant FindParticipant(Room room, Guid participantId) =>
        room.Participants.TryGetValue(participantId, out var participant)
            ? participant
            : throw new NotFoundException($"Participant {participantId} not found in room {room.Id}.");

    private void EndParticipant(Room room, Guid participantId, string reason)
    {
        room.Participants.Remove(participantId);
        joinTokens.DeleteForParticipant(participantId);
        activity.SessionEnded(participantId, reason);
        activity.Event("participant.left", room.Id, participantId, reason);
    }

    private sealed class Room(RoomRecord record)
    {
        public Guid Id { get; } = record.Id;
        public string Name { get; } = record.Name;
        public string? ExternalId { get; } = record.ExternalId;
        public int MaxParticipants { get; } = record.MaxParticipants;
        public string? Metadata { get; } = record.MetadataJson;
        public DateTimeOffset CreatedAt { get; } = record.CreatedAt;
        public Dictionary<Guid, Participant> Participants { get; } = [];

        public RoomSnapshot Snapshot() =>
            new(Id, Name, ExternalId, MaxParticipants, ParseMetadata(Metadata), CreatedAt, Participants.Count);

        public IReadOnlyList<ParticipantSnapshot> ParticipantSnapshots() =>
            Participants.Values.OrderBy(p => p.JoinedAt).Select(p => p.Snapshot()).ToList();
    }

    private sealed class Participant
    {
        public required Guid Id { get; init; }
        public required Guid RoomId { get; init; }
        public required string DisplayName { get; init; }
        public string? ExternalId { get; init; }
        public string? Metadata { get; init; }
        public required DateTimeOffset JoinedAt { get; init; }
        public Vec3? Position { get; set; }
        public Quat? Orientation { get; set; }
        public bool Muted { get; set; }
        public double Gain { get; set; } = 1.0;
        public ParticipantState State { get; set; } = ParticipantState.AwaitingSignal;

        public ParticipantSnapshot Snapshot() =>
            new(Id, RoomId, DisplayName, ExternalId, Position, Orientation, Muted, Gain, ParseMetadata(Metadata), JoinedAt, State);
    }

    // Stored as text and parsed per snapshot, so callers never share a mutable JsonObject with the manager.
    private static JsonObject? ParseMetadata(string? json) => json is null ? null : JsonNode.Parse(json) as JsonObject;
}
