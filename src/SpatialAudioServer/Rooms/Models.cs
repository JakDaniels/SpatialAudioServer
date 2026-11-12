using System.Text.Json.Nodes;

namespace SpatialAudioServer.Rooms;

/// <summary>Position in the room's coordinate space (region metres for SL). Stored now, used by the spatial phase.</summary>
public sealed record Vec3(double X, double Y, double Z);

/// <summary>Orientation as a quaternion (the form SL/OpenSim use for avatar rotation).</summary>
public sealed record Quat(double X, double Y, double Z, double W);

public enum ParticipantState
{
    /// <summary>Created via the API; the client has not signalled yet.</summary>
    AwaitingSignal,
    Connecting,
    Connected,
    Disconnected,
    Failed,
}

public sealed record RoomSnapshot(
    Guid Id,
    string Name,
    string? ExternalId,
    int MaxParticipants,
    JsonObject? Metadata,
    DateTimeOffset CreatedAt,
    int ParticipantCount,
    string Engine);

public sealed record ParticipantSnapshot(
    Guid Id,
    Guid RoomId,
    string DisplayName,
    string? ExternalId,
    Vec3? Position,
    Quat? Orientation,
    bool Muted,
    double Gain,
    JsonObject? Metadata,
    DateTimeOffset JoinedAt,
    ParticipantState State);

/// <param name="Engine">Mix engine name (see <c>GET /api/status</c>); null uses the server default (Media:Engine).</param>
public sealed record CreateRoomCommand(string? Name, string? ExternalId, int? MaxParticipants, JsonObject? Metadata, string? Engine = null);

/// <summary>Body of get-or-create by external id. Only used when the room is created; name defaults to the external id.</summary>
public sealed record EnsureRoomCommand(string? Name, int? MaxParticipants, JsonObject? Metadata, string? Engine = null);

public sealed record AddParticipantCommand(
    string? DisplayName, string? ExternalId, Vec3? Position, Quat? Orientation, JsonObject? Metadata);

/// <summary>Partial update: null fields are left unchanged.</summary>
public sealed record UpdateParticipantCommand(bool? Muted, double? Gain, Vec3? Position, Quat? Orientation);

/// <param name="ReplacedParticipantId">
/// Set when a participant with the same externalId was already in the room and has been replaced (treated as a reconnect).
/// </param>
public sealed record JoinResult(ParticipantSnapshot Participant, string JoinToken, DateTimeOffset ExpiresAt, Guid? ReplacedParticipantId);

public sealed class NotFoundException(string message) : Exception(message);

/// <summary>The request clashes with existing state (e.g. a duplicate room externalId).</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>
/// External ids are caller-defined strings (OpenSim region/parcel/avatar UUIDs in practice). Anything that parses
/// as a GUID is normalised to lowercase "D" form, so "{ABC…}" and "abc…" address the same room or participant.
/// </summary>
public static class ExternalIds
{
    public static string? Normalize(string? value) =>
        value is null ? null
        : Guid.TryParse(value, out var guid) ? guid.ToString("D")
        : value.Trim();
}

public sealed class LimitExceededException(string message) : Exception(message);

public sealed class DomainValidationException(IDictionary<string, string[]> errors)
    : Exception("Validation failed: " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}")))
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}
