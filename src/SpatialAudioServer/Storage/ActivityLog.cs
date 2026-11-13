using static SpatialAudioServer.Storage.StoreHelpers;

namespace SpatialAudioServer.Storage;

public sealed record EventRecord(long Id, DateTimeOffset At, string Kind, Guid? RoomId, Guid? ParticipantId, string? Detail);

public sealed record SessionRecord(
    Guid ParticipantId, Guid RoomId, string? ExternalId, string DisplayName,
    DateTimeOffset JoinedAt, DateTimeOffset? LeftAt, string? LeaveReason);

/// <summary>Participant session history and the event log shown in the admin UI.</summary>
public sealed class ActivityLog(Database database, TimeProvider time)
{
    public void Event(string kind, Guid? roomId = null, Guid? participantId = null, string? detail = null)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "INSERT INTO events (at, kind, room_id, participant_id, detail) VALUES ($at, $kind, $room, $pid, $detail)",
            ("$at", ToDb(time.GetUtcNow())), ("$kind", kind), ("$room", roomId?.ToString()),
            ("$pid", participantId?.ToString()), ("$detail", detail));
        command.ExecuteNonQuery();
    }

    /// <summary>Newest first; optionally one room's, and only those older than event <paramref name="before"/> (paging).</summary>
    public IReadOnlyList<EventRecord> RecentEvents(int limit = 100, Guid? roomId = null, long? before = null)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT id, at, kind, room_id, participant_id, detail FROM events " +
            "WHERE ($room IS NULL OR room_id = $room) AND ($before IS NULL OR id < $before) ORDER BY id DESC LIMIT $limit",
            ("$limit", limit), ("$room", roomId?.ToString()), ("$before", before));
        using var reader = command.ExecuteReader();
        var events = new List<EventRecord>();
        while (reader.Read())
        {
            events.Add(new EventRecord(
                reader.GetInt64(0), FromDb(reader.GetString(1)), reader.GetString(2),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.GetNullableString(5)));
        }
        return events;
    }

    public void SessionStarted(Guid participantId, Guid roomId, string? externalId, string displayName, DateTimeOffset joinedAt)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "INSERT INTO sessions (participant_id, room_id, external_id, display_name, joined_at) " +
            "VALUES ($pid, $room, $external, $name, $joined)",
            ("$pid", participantId.ToString()), ("$room", roomId.ToString()), ("$external", externalId),
            ("$name", displayName), ("$joined", ToDb(joinedAt)));
        command.ExecuteNonQuery();
    }

    public void SessionEnded(Guid participantId, string reason)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "UPDATE sessions SET left_at = $now, leave_reason = $reason WHERE participant_id = $pid AND left_at IS NULL",
            ("$now", ToDb(time.GetUtcNow())), ("$reason", reason), ("$pid", participantId.ToString()));
        command.ExecuteNonQuery();
    }

    /// <summary>Closes sessions left open by a previous run (crash or restart). Returns how many.</summary>
    public int CloseOpenSessions(string reason)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "UPDATE sessions SET left_at = $now, leave_reason = $reason WHERE left_at IS NULL",
            ("$now", ToDb(time.GetUtcNow())), ("$reason", reason));
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<SessionRecord> Sessions(Guid roomId)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT participant_id, room_id, external_id, display_name, joined_at, left_at, leave_reason " +
            "FROM sessions WHERE room_id = $room ORDER BY joined_at",
            ("$room", roomId.ToString()));
        using var reader = command.ExecuteReader();
        var sessions = new List<SessionRecord>();
        while (reader.Read())
        {
            sessions.Add(new SessionRecord(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetNullableString(2),
                reader.GetString(3), FromDb(reader.GetString(4)), FromDbNullable(reader, 5), reader.GetNullableString(6)));
        }
        return sessions;
    }
}
