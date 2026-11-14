using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Media;
using SpatialAudioServer.Media.Engines;
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
    IOptions<MediaOptions> mediaOptions,
    TimeProvider time,
    ILogger<RoomManager> log)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Room> _rooms = [];
    private bool _restored;
    private bool _stopping;

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
            media.ParticipantStateChanged += SetParticipantState;
            media.ParticipantData += ReceiveClientData;

            var closed = activity.CloseOpenSessions("server-restart");
            joinTokens.DeleteAll();
            foreach (var record in rooms.All())
            {
                if (record.Engine is { } engine && !MixEngines.Exists(engine))
                {
                    log.LogWarning("Room {RoomId} uses mix engine '{Engine}', which this server does not have; using {Default}",
                        record.Id, engine, DefaultEngine);
                }
                _rooms[record.Id] = new Room(record, DefaultEngine);
            }
            restored = _rooms.Values.Select(r => r.Snapshot()).ToList();
            log.LogInformation("Restored {Rooms} rooms; closed {Sessions} sessions left open by the previous run",
                restored.Count, closed);
        }
        restored.ForEach(media.RoomCreated);
    }

    /// <summary>
    /// The server is stopping: ends every participant's session (reason "server-shutdown"; their media stops with the
    /// room pipelines) and refuses new participants. Returns how many were ended.
    /// </summary>
    public int Shutdown()
    {
        lock (_lock)
        {
            _stopping = true;
            var ended = 0;
            foreach (var room in _rooms.Values)
            {
                foreach (var pid in room.Participants.Keys.ToList())
                {
                    EndParticipant(room, pid, "server-shutdown");
                    ended++;
                }
            }
            return ended;
        }
    }

    /// <summary>Creates a room. A room with the same externalId already existing is a <see cref="ConflictException"/>.</summary>
    public RoomSnapshot CreateRoom(CreateRoomCommand command)
    {
        command = command with { ExternalId = ExternalIds.Normalize(command.ExternalId) };
        ValidateCreate(command);

        RoomSnapshot snapshot;
        lock (_lock)
        {
            if (command.ExternalId is { } externalId && FindRoomByExternalId(externalId) is not null)
            {
                throw new ConflictException($"A room with externalId '{externalId}' already exists.");
            }
            snapshot = CreateRoomLocked(command);
        }
        RoomCreated(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Get-or-create by external id (e.g. the region module at region start). Returns the existing room unchanged,
    /// or creates one from <paramref name="command"/>, whose name defaults to the external id.
    /// </summary>
    public (RoomSnapshot Room, bool Created) EnsureRoom(string externalId, EnsureRoomCommand? command)
    {
        var normalized = ExternalIds.Normalize(externalId);
        var create = new CreateRoomCommand(command?.Name ?? normalized, normalized, command?.MaxParticipants, command?.Metadata, command?.Engine, command?.Spatial, command?.ClientPose);
        ValidateCreate(create);

        RoomSnapshot snapshot;
        lock (_lock)
        {
            if (FindRoomByExternalId(normalized!) is { } existing)
            {
                return (existing.Snapshot(), false);
            }
            snapshot = CreateRoomLocked(create);
        }
        RoomCreated(snapshot);
        return (snapshot, true);
    }

    /// <summary>The id of the room with this external id; <see cref="NotFoundException"/> if none.</summary>
    public Guid ResolveRoom(string externalId)
    {
        var normalized = ExternalIds.Normalize(externalId)!;
        lock (_lock)
        {
            return FindRoomByExternalId(normalized)?.Id
                ?? throw new NotFoundException($"No room with externalId '{normalized}'.");
        }
    }

    /// <summary>The id of the participant with this external id in the room; <see cref="NotFoundException"/> if none.</summary>
    public Guid ResolveParticipant(Guid roomId, string externalId)
    {
        var normalized = ExternalIds.Normalize(externalId)!;
        lock (_lock)
        {
            return FindParticipantByExternalId(FindRoom(roomId), normalized)?.Id
                ?? throw new NotFoundException($"No participant with externalId '{normalized}' in room {roomId}.");
        }
    }

    public IReadOnlyList<RoomSnapshot> ListRooms(string? externalId = null)
    {
        externalId = ExternalIds.Normalize(externalId);
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

    /// <summary>
    /// Adds a participant and issues its join token. If a participant with the same externalId is already in the
    /// room it is removed first (reason "replaced") and the new one takes its place: a re-add is a reconnect, which
    /// also covers a region module that missed a leave event.
    /// </summary>
    public JoinResult AddParticipant(Guid roomId, AddParticipantCommand command)
    {
        command = command with { ExternalId = ExternalIds.Normalize(command.ExternalId) };
        new Validation()
            .Required("displayName", command.DisplayName, Validation.MaxNameLength)
            .Optional("externalId", command.ExternalId, Validation.MaxExternalIdLength)
            .Position("position", command.Position)
            .Orientation("orientation", command.Orientation)
            .Metadata("metadata", command.Metadata)
            .ThrowIfInvalid();

        JoinResult result;
        Guid? replaced = null;
        lock (_lock)
        {
            if (_stopping)
            {
                throw new ServiceUnavailableException("The server is shutting down.");
            }
            var room = FindRoom(roomId);
            if (command.ExternalId is { } externalId && FindParticipantByExternalId(room, externalId) is { } previous)
            {
                replaced = previous.Id;
                EndParticipant(room, previous.Id, "replaced");
            }
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
                ClientNamed = command.ClientNamed ?? false,
            };
            participant.StateSince = participant.JoinedAt;
            var (token, expires) = joinTokens.Issue(participant.Id, roomId, auth.Value.JoinTokenLifetime);
            activity.SessionStarted(participant.Id, roomId, participant.ExternalId, participant.DisplayName, participant.JoinedAt);
            activity.Event("participant.joined", roomId, participant.Id, participant.DisplayName);
            room.Participants[participant.Id] = participant;
            result = new JoinResult(participant.Snapshot(), token, expires, replaced);
        }
        if (replaced is { } old)
        {
            log.LogInformation("Participant {Old} replaced by {New} (same externalId)", old, result.Participant.Id);
            media.ParticipantRemoved(roomId, old);
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
            .Position("listenerPosition", command.ListenerPosition)
            .Orientation("listenerOrientation", command.ListenerOrientation)
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
            participant.ListenerPosition = command.ListenerPosition ?? participant.ListenerPosition;
            participant.ListenerOrientation = command.ListenerOrientation ?? participant.ListenerOrientation;
            snapshot = participant.Snapshot();
        }
        media.ParticipantUpdated(snapshot);
        return snapshot;
    }

    /// <summary>Most poses in one batch: more than a room can hold, so a real batch never hits it.</summary>
    public const int MaxPoseBatch = 256;

    /// <summary>
    /// Sets many participants' positions and orientations at once (the simulator's avatar positions). Unknown ids are
    /// reported, not errors: an avatar may leave between the simulator's snapshot and this call.
    /// </summary>
    public PoseBatchResult UpdatePoses(Guid roomId, PoseBatchCommand command)
    {
        var poses = command.Poses ?? [];
        var validation = new Validation();
        if (poses.Count > MaxPoseBatch)
        {
            throw new LimitExceededException($"At most {MaxPoseBatch} poses per request.");
        }
        for (var i = 0; i < poses.Count; i++)
        {
            var pose = poses[i];
            var oneId = pose is not null && (pose.ParticipantId is null) != string.IsNullOrWhiteSpace(pose.ExternalId);
            validation.Check($"poses[{i}]", oneId, "needs exactly one of participantId and externalId");
            if (!oneId)
            {
                continue;
            }
            validation.Position($"poses[{i}].position", pose!.Position).Orientation($"poses[{i}].orientation", pose.Orientation);
        }
        validation.ThrowIfInvalid();

        var updated = new List<ParticipantSnapshot>();
        var unknown = new List<string>();
        lock (_lock)
        {
            var room = FindRoom(roomId);
            foreach (var pose in poses)
            {
                var participant = pose.ParticipantId is { } id
                    ? room.Participants.GetValueOrDefault(id)
                    : FindParticipantByExternalId(room, ExternalIds.Normalize(pose.ExternalId)!);
                if (participant is null)
                {
                    unknown.Add(pose.ParticipantId?.ToString() ?? pose.ExternalId!);
                    continue;
                }
                participant.Position = pose.Position ?? participant.Position;
                participant.Orientation = pose.Orientation ?? participant.Orientation;
                updated.Add(participant.Snapshot());
            }
        }
        foreach (var snapshot in updated)
        {
            media.ParticipantUpdated(snapshot);
        }
        return new PoseBatchResult(updated.Count, unknown);
    }

    /// <summary>
    /// A participant choosing its own display name (signalling: sent with its offer). Only participants created with
    /// <c>clientNamed</c> (invited guests) may; a name given by the integration (an SL avatar's) stands.
    /// </summary>
    public ParticipantSnapshot RenameSelf(Guid roomId, Guid participantId, string? displayName)
    {
        new Validation().Required("displayName", displayName, Validation.MaxNameLength).ThrowIfInvalid();
        var name = new string(displayName!.Trim().Where(c => !char.IsControl(c)).ToArray());
        if (name.Length == 0)
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["displayName"] = ["must not be empty"] });
        }
        lock (_lock)
        {
            var participant = FindParticipant(FindRoom(roomId), participantId);
            if (participant.DisplayName == name)
            {
                return participant.Snapshot();
            }
            if (!participant.ClientNamed)
            {
                throw new ForbiddenException("This participant's name is set by the server and cannot be changed by the client.");
            }
            activity.Event("participant.renamed", roomId, participantId, $"{participant.DisplayName} → {name}");
            activity.SessionRenamed(participantId, name);
            participant.DisplayName = name;
            log.LogInformation("Participant {ParticipantId} in room {RoomId} is now called '{Name}'", participantId, roomId, name);
            return participant.Snapshot();
        }
    }

    /// <summary>The shortest interval between a participant's own pose updates (20 per second; SL viewers send ≤ 10).</summary>
    public static readonly TimeSpan SelfPoseInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// A participant changing its own pose (signalling, authorised by its session token), within what the room's
    /// <see cref="ClientPose"/> allows: today, turning only. Moving is for the control API (the region module, which knows
    /// where avatars really are); letting clients move would let them walk up to any conversation.
    /// </summary>
    public ParticipantSnapshot UpdateOwnPose(Guid roomId, Guid participantId, SelfPoseCommand command)
    {
        new Validation().Orientation("listenerOrientation", command.ListenerOrientation).ThrowIfInvalid();
        ParticipantSnapshot snapshot;
        lock (_lock)
        {
            var room = FindRoom(roomId);
            var participant = FindParticipant(room, participantId);
            if (command.ListenerPosition is not null)
            {
                throw new ForbiddenException("Participants cannot move themselves in this room; positions come from the control API.");
            }
            if (command.ListenerOrientation is not null && room.ClientPose < ClientPose.Orientation)
            {
                throw new ForbiddenException("This room does not let participants turn themselves (clientPose is none).");
            }
            var now = time.GetUtcNow();
            if (now - participant.LastSelfPose < SelfPoseInterval)
            {
                throw new RateLimitedException($"At most one pose update per {SelfPoseInterval.TotalMilliseconds:F0} ms.");
            }
            participant.LastSelfPose = now;
            participant.ListenerOrientation = command.ListenerOrientation ?? participant.ListenerOrientation;
            snapshot = participant.Snapshot();
        }
        media.ParticipantUpdated(snapshot);
        return snapshot;
    }

    /// <summary>Client data messages closer together than this are dropped (SL viewers send poses ≤ 10 times a second).</summary>
    public static readonly TimeSpan ClientPoseInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Called by the media layer (on a GStreamer thread) for each message on a participant's data channel: an SL
    /// viewer's SLData. Poses are used in <see cref="ClientPose.Tethered"/> rooms; the rest is logged for now.
    /// </summary>
    public void ReceiveClientData(Guid roomId, Guid participantId, string text)
    {
        if (SlData.Parse(text) is not { } message)
        {
            log.LogDebug("Participant {ParticipantId}: data channel message that is not SLData: {Text}", participantId, Shorten(text));
            return;
        }
        if (message.Joined || message.Mutes.Count > 0 || message.Gains.Count > 0 || message.OtherKeys.Count > 0)
        {
            log.LogInformation("Participant {ParticipantId} SLData: {Text}", participantId, Shorten(text));
        }
        else
        {
            log.LogTrace("Participant {ParticipantId} SLData: {Text}", participantId, Shorten(text));
        }
        if (message.Pose is not { } pose)
        {
            return;
        }
        ParticipantSnapshot snapshot;
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out var room) || !room.Participants.TryGetValue(participantId, out var participant))
            {
                return;
            }
            if (room.ClientPose != ClientPose.Tethered)
            {
                if (!participant.PoseIgnoredLogged)
                {
                    participant.PoseIgnoredLogged = true;
                    log.LogInformation("Participant {ParticipantId} sends its pose, but room {RoomId} is clientPose {ClientPose}, not tethered: ignored",
                        participantId, roomId, room.ClientPose);
                }
                return;
            }
            var now = time.GetUtcNow();
            if (now - participant.LastSelfPose < ClientPoseInterval)
            {
                return;
            }
            participant.LastSelfPose = now;
            participant.ClientReport = pose;
            snapshot = participant.Snapshot();
        }
        media.ParticipantUpdated(snapshot);
    }

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";

    /// <summary>Called by the media layer (on a GStreamer thread) as a participant's WebRTC connection changes.</summary>
    public void SetParticipantState(Guid roomId, Guid participantId, ParticipantState state)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out var room)
                || !room.Participants.TryGetValue(participantId, out var participant)
                || participant.State == state)
            {
                return;
            }
            participant.State = state;
            participant.StateSince = time.GetUtcNow();
            if (state is ParticipantState.Connected or ParticipantState.Disconnected or ParticipantState.Failed)
            {
                activity.Event($"participant.{state.ToString().ToLowerInvariant()}", roomId, participantId);
            }
        }
        log.LogInformation("Participant {ParticipantId} in room {RoomId} is {State}", participantId, roomId, state);
    }

    /// <summary>
    /// Removes participants whose connection has been failed or disconnected for at least <paramref name="timeout"/>
    /// (a client that went away without leaving). Returns how many were removed.
    /// </summary>
    public int DropDeadConnections(TimeSpan timeout)
    {
        List<(Guid Room, Guid Participant, ParticipantState State, TimeSpan For)> dead;
        var now = time.GetUtcNow();
        lock (_lock)
        {
            dead = _rooms.Values
                .SelectMany(r => r.Participants.Values)
                .Where(p => p.State is ParticipantState.Failed or ParticipantState.Disconnected && now - p.StateSince >= timeout)
                .Select(p => (p.RoomId, p.Id, p.State, now - p.StateSince))
                .ToList();
        }
        var removed = 0;
        foreach (var (roomId, participantId, state, duration) in dead)
        {
            try
            {
                log.LogWarning("Participant {ParticipantId} in room {RoomId} has been {State} for {Seconds:F0} s; removing it",
                    participantId, roomId, state, duration.TotalSeconds);
                RemoveParticipant(roomId, participantId, "connection-lost");
                removed++;
            }
            catch (NotFoundException)
            {
                // Left or was removed since the scan.
            }
        }
        return removed;
    }

    /// <summary>
    /// Removes participants whose join token expired unused (reason "join-expired"): their client never connected and
    /// now never can. Also clears used and expired tokens from the database. Returns how many were removed.
    /// </summary>
    public int DropExpiredJoins()
    {
        var removed = 0;
        foreach (var claim in joinTokens.PurgeDead())
        {
            try
            {
                log.LogWarning("Participant {ParticipantId} in room {RoomId} never connected and its join token has expired; removing it",
                    claim.ParticipantId, claim.RoomId);
                RemoveParticipant(claim.RoomId, claim.ParticipantId, "join-expired");
                removed++;
            }
            catch (NotFoundException)
            {
                // Removed meanwhile.
            }
        }
        return removed;
    }

    private void ValidateCreate(CreateRoomCommand command) =>
        new Validation()
            .Required("name", command.Name, Validation.MaxNameLength)
            .Optional("externalId", command.ExternalId, Validation.MaxExternalIdLength)
            .Range("maxParticipants", command.MaxParticipants, 1, limits.Value.MaxParticipantsPerRoom)
            .Metadata("metadata", command.Metadata)
            .OneOf("engine", command.Engine, MixEngines.Exists, MixEngines.All.Select(e => e.Name))
            .ThrowIfInvalid();

    private RoomSnapshot CreateRoomLocked(CreateRoomCommand command)
    {
        var limit = limits.Value;
        if (_rooms.Count >= limit.MaxRooms)
        {
            throw new LimitExceededException($"The server already has the maximum of {limit.MaxRooms} rooms.");
        }
        var record = new RoomRecord(
            Guid.NewGuid(), command.Name!.Trim(), command.ExternalId, command.MaxParticipants ?? limit.MaxParticipantsPerRoom,
            command.Metadata?.ToJsonString(), time.GetUtcNow(),
            command.Engine ?? (command.Spatial == true ? mediaOptions.Value.SpatialEngine : DefaultEngine),
            command.Spatial ?? false, command.ClientPose?.ToString());
        rooms.Insert(record);
        var room = new Room(record, DefaultEngine);
        _rooms[room.Id] = room;
        activity.Event("room.created", room.Id, detail: room.Name);
        return room.Snapshot();
    }

    /// <summary>The configured default engine; an unknown name is a configuration error, caught at start-up.</summary>
    private string DefaultEngine => mediaOptions.Value.Engine;

    private void RoomCreated(RoomSnapshot snapshot)
    {
        log.LogInformation("Room {RoomId} '{Name}' created (engine {Engine}{Spatial})", snapshot.Id, snapshot.Name, snapshot.Engine,
            snapshot.Spatial ? ", spatial" : "");
        media.RoomCreated(snapshot);
    }

    private Room? FindRoomByExternalId(string externalId) =>
        _rooms.Values.FirstOrDefault(r => r.ExternalId == externalId);

    private static Participant? FindParticipantByExternalId(Room room, string externalId) =>
        room.Participants.Values.FirstOrDefault(p => p.ExternalId == externalId);

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

    private sealed class Room(RoomRecord record, string defaultEngine)
    {
        public Guid Id { get; } = record.Id;
        public string Name { get; } = record.Name;
        public string? ExternalId { get; } = record.ExternalId;
        public int MaxParticipants { get; } = record.MaxParticipants;
        public string? Metadata { get; } = record.MetadataJson;
        public DateTimeOffset CreatedAt { get; } = record.CreatedAt;
        public string Engine { get; } = record.Engine is { } e && MixEngines.Exists(e) ? e : defaultEngine;
        public bool Spatial { get; } = record.Spatial;
        public ClientPose ClientPose { get; } = Enum.TryParse<ClientPose>(record.ClientPose, ignoreCase: true, out var pose)
            ? pose
            : record.Spatial ? ClientPose.Orientation : ClientPose.None;
        public Dictionary<Guid, Participant> Participants { get; } = [];

        public RoomSnapshot Snapshot() =>
            new(Id, Name, ExternalId, MaxParticipants, ParseMetadata(Metadata), CreatedAt, Participants.Count, Engine, Spatial, ClientPose);

        public IReadOnlyList<ParticipantSnapshot> ParticipantSnapshots() =>
            Participants.Values.OrderBy(p => p.JoinedAt).Select(p => p.Snapshot()).ToList();
    }

    private sealed class Participant
    {
        public required Guid Id { get; init; }
        public required Guid RoomId { get; init; }
        public required string DisplayName { get; set; }
        /// <summary>May choose its own display name (an invited guest).</summary>
        public bool ClientNamed { get; init; }
        public string? ExternalId { get; init; }
        public string? Metadata { get; init; }
        public required DateTimeOffset JoinedAt { get; init; }
        public Vec3? Position { get; set; }
        public Quat? Orientation { get; set; }
        public Vec3? ListenerPosition { get; set; }
        public Quat? ListenerOrientation { get; set; }
        /// <summary>When the participant last set its own pose (rate limit).</summary>
        public DateTimeOffset LastSelfPose { get; set; }
        /// <summary>The viewer's own report of where it is (tethered rooms).</summary>
        public ClientPoseReport? ClientReport { get; set; }
        public bool PoseIgnoredLogged { get; set; }
        public bool Muted { get; set; }
        public double Gain { get; set; } = 1.0;
        public ParticipantState State { get; set; } = ParticipantState.AwaitingSignal;
        /// <summary>When <see cref="State"/> last changed (set to the join time at creation).</summary>
        public DateTimeOffset StateSince { get; set; }

        public ParticipantSnapshot Snapshot() =>
            new(Id, RoomId, DisplayName, ExternalId, Position, Orientation, Muted, Gain, ParseMetadata(Metadata), JoinedAt, State,
                ListenerPosition, ListenerOrientation, ClientNamed, ClientReport);
    }

    // Stored as text and parsed per snapshot, so callers never share a mutable JsonObject with the manager.
    private static JsonObject? ParseMetadata(string? json) => json is null ? null : JsonNode.Parse(json) as JsonObject;
}
