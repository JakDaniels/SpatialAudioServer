using System.Text.Json.Nodes;

namespace SpatialAudioServer.Rooms;

/// <summary>Position in the room's coordinate space (region metres for SL). Stored now, used by the spatial phase.</summary>
public sealed record Vec3(double X, double Y, double Z);

/// <summary>Orientation as a quaternion (the form SL/OpenSim use for avatar rotation).</summary>
public sealed record Quat(double X, double Y, double Z, double W);

/// <summary>
/// What a participant may change about its own pose through signalling (its session token), as opposed to the control
/// API, which may change anything. Spatial rooms default to <see cref="Orientation"/>, others to <see cref="None"/>.
/// (Planned with the SL viewer work, PLAN.md Phase 7: tethered, where the listening position may move within a radius
/// of the server-set position; and free.)
/// </summary>
public enum ClientPose
{
    /// <summary>Only the control API sets poses.</summary>
    None,

    /// <summary>The participant may turn (set its listener orientation), not move: it cannot go and eavesdrop.</summary>
    Orientation,
}

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
    string Engine,
    bool Spatial = false,
    ClientPose ClientPose = ClientPose.None);

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
    ParticipantState State,
    Vec3? ListenerPosition = null,
    Quat? ListenerOrientation = null,
    bool ClientNamed = false);

/// <param name="Engine">Mix engine name (see <c>GET /api/status</c>); null uses the server default (Media:Engine).</param>
/// <param name="Spatial">Mix by participant position (distance and direction); fixed for the room's life. Default off.</param>
/// <param name="ClientPose">What participants may set about their own pose; default by <paramref name="Spatial"/>.</param>
public sealed record CreateRoomCommand(
    string? Name, string? ExternalId, int? MaxParticipants, JsonObject? Metadata, string? Engine = null, bool? Spatial = null,
    ClientPose? ClientPose = null);

/// <summary>Body of get-or-create by external id. Only used when the room is created; name defaults to the external id.</summary>
public sealed record EnsureRoomCommand(
    string? Name, int? MaxParticipants, JsonObject? Metadata, string? Engine = null, bool? Spatial = null, ClientPose? ClientPose = null);

/// <param name="ClientNamed">
/// The participant may choose its own display name when it joins (an invited guest); otherwise the name given here
/// stands (e.g. an SL avatar's name from the region module). Default false.
/// </param>
public sealed record AddParticipantCommand(
    string? DisplayName, string? ExternalId, Vec3? Position, Quat? Orientation, JsonObject? Metadata, bool? ClientNamed = null);

/// <summary>Partial update: null fields are left unchanged.</summary>
/// <param name="Position">Where the participant is: where others hear it from (an SL avatar's position).</param>
/// <param name="ListenerPosition">Where it hears from, if not <paramref name="Position"/> (an SL viewer's camera).</param>
/// <param name="ListenerOrientation">Which way it faces for hearing, if not <paramref name="Orientation"/>.</param>
public sealed record UpdateParticipantCommand(
    bool? Muted, double? Gain, Vec3? Position, Quat? Orientation, Vec3? ListenerPosition = null, Quat? ListenerOrientation = null);

/// <summary>A participant's change to its own pose (signalling, session token), within the room's <see cref="ClientPose"/>.</summary>
public sealed record SelfPoseCommand(Quat? ListenerOrientation, Vec3? ListenerPosition = null);

/// <summary>The caller is identified but not allowed to do this (e.g. move where the room only lets it turn).</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>Too many requests of this kind; try again shortly.</summary>
public sealed class RateLimitedException(string message) : Exception(message);

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
