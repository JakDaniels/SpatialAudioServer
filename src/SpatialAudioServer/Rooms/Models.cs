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
}

public sealed record RoomSnapshot(
    Guid Id,
    string Name,
    string? ExternalId,
    int MaxParticipants,
    JsonObject? Metadata,
    DateTimeOffset CreatedAt,
    int ParticipantCount);

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

public sealed record CreateRoomCommand(string? Name, string? ExternalId, int? MaxParticipants, JsonObject? Metadata);

public sealed record AddParticipantCommand(
    string? DisplayName, string? ExternalId, Vec3? Position, Quat? Orientation, JsonObject? Metadata);

/// <summary>Partial update: null fields are left unchanged.</summary>
public sealed record UpdateParticipantCommand(bool? Muted, double? Gain, Vec3? Position, Quat? Orientation);

public sealed record JoinResult(ParticipantSnapshot Participant, string JoinToken, DateTimeOffset ExpiresAt);

public sealed class NotFoundException(string message) : Exception(message);

public sealed class LimitExceededException(string message) : Exception(message);

public sealed class DomainValidationException(IDictionary<string, string[]> errors)
    : Exception("Validation failed: " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}")))
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}
