// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace SpatialAudioServer.Rooms;

/// <summary>Position in the room's coordinate space (region metres for SL). Stored now, used by the spatial phase.</summary>
public sealed record Vec3(double X, double Y, double Z);

/// <summary>Orientation as a quaternion (the form SL/OpenSim use for avatar rotation).</summary>
public sealed record Quat(double X, double Y, double Z, double W);

/// <summary>
/// What a participant may change about its own pose through signalling (its session token), as opposed to the control
/// API, which may change anything. Spatial rooms default to <see cref="Orientation"/>, others to <see cref="None"/>.
/// </summary>
public enum ClientPose
{
    /// <summary>Only the control API sets poses.</summary>
    None,

    /// <summary>The participant may turn (set its listener orientation), not move: it cannot go and eavesdrop.</summary>
    Orientation,

    /// <summary>
    /// SL/OpenSim rooms: the viewer reports where its avatar and its camera are (SLData <c>sp/sh/lp/lh</c>), and the
    /// server uses that only near the position the control API (the simulator) gives: the avatar is heard from the
    /// viewer's position while it is within <c>Media:Spatial:SpeakerTether</c> of the simulator's, otherwise from the
    /// simulator's; the camera hears from up to <c>Media:Spatial:ListenerTether</c> away (SL's camera tether).
    /// </summary>
    Tethered,
}

/// <summary>
/// Whether the integration (the OpenSim region module) shows web and bot participants in its world, as NPCs. Set on
/// the room by the integration and reported to clients (<c>GET /signal/{id}/config</c>), so a web client knows whether
/// to offer it; a client asks with its first offer (<see cref="ParticipantSnapshot.InWorldAvatar"/>). The server only
/// stores and reports both: the integration decides and creates the avatar.
/// </summary>
public enum InWorldAvatars
{
    /// <summary>Not offered.</summary>
    Off,

    /// <summary>A participant may ask for one (the web client's tick box).</summary>
    Optional,

    /// <summary>Every web and bot participant gets one: no invisible listeners in the region.</summary>
    Always,
}

/// <summary>
/// How one listener wants one other participant: muted, or at a volume (1 = as mixed), on top of the room's own
/// gain and mute for that participant. Set by an SL viewer's SLData <c>m</c> and <c>ug</c> (its mute list and the
/// per-person volume slider), so it only changes that listener's mix.
/// </summary>
public sealed record PeerSetting(bool Muted, double Gain)
{
    public static readonly PeerSetting Default = new(false, 1);
}

/// <summary>Where a viewer says its avatar (speaker) and its camera (listener) are, in room coordinates.</summary>
public sealed record ClientPoseReport(Vec3 SpeakerPosition, Quat SpeakerOrientation, Vec3 ListenerPosition, Quat ListenerOrientation);

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
    ClientPose ClientPose = ClientPose.None,
    InWorldAvatars InWorldAvatars = InWorldAvatars.Off,
    string? SpatialProfile = null,
    string? Acoustics = null);

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
    bool ClientNamed = false,
    ClientPoseReport? ClientReport = null,
    IReadOnlyDictionary<string, PeerSetting>? Peers = null,
    bool InWorldAvatar = false,
    Guid? AvatarId = null)
{
    /// <summary>
    /// How SL viewers know this participant: the in-world avatar the integration made for it (<see cref="AvatarId"/>,
    /// an NPC), else its avatar UUID when the external id is one, otherwise its participant id (a web browser or bot).
    /// Lower case, as viewers write UUIDs. <see cref="Peers"/> is keyed by it.
    /// </summary>
    public string ViewerKey => AvatarId?.ToString() ?? (Guid.TryParse(ExternalId, out var avatar) ? avatar.ToString() : Id.ToString());
}

/// <param name="Engine">Mix engine name (see <c>GET /api/status</c>); null uses the server default (Media:Engine).</param>
/// <param name="Spatial">Mix by participant position (distance and direction); fixed for the room's life. Default off.</param>
/// <param name="ClientPose">What participants may set about their own pose; default by <paramref name="Spatial"/>.</param>
/// <param name="InWorldAvatars">Whether the integration shows web and bot participants in-world; default off.</param>
/// <param name="SpatialProfile">Spatial rooms: the named distance curve (<c>Media:Spatial:Profiles</c>, e.g. "outdoor",
/// "meeting"); null = the server's default curve. Fixed for the room's life.</param>
/// <param name="Acoustics">The kind of space, for its early reflections (<c>outdoor</c>, the default: none;
/// <c>small-room</c>, <c>hall</c>, <c>cave</c>); only the <c>acoustic</c> engine renders it. May change later.</param>
public sealed record CreateRoomCommand(
    string? Name, string? ExternalId, int? MaxParticipants, JsonObject? Metadata, string? Engine = null, bool? Spatial = null,
    ClientPose? ClientPose = null, InWorldAvatars? InWorldAvatars = null, string? SpatialProfile = null, string? Acoustics = null);

/// <summary>Body of get-or-create by external id. Only used when the room is created; name defaults to the external id.</summary>
public sealed record EnsureRoomCommand(
    string? Name, int? MaxParticipants, JsonObject? Metadata, string? Engine = null, bool? Spatial = null, ClientPose? ClientPose = null,
    InWorldAvatars? InWorldAvatars = null, string? SpatialProfile = null, string? Acoustics = null);

/// <summary>Viewer keys (UUIDs) that have gone from a room, e.g. NPCs the integration removed (SL viewers drop them).</summary>
public sealed record ViewerKeysGoneCommand(IReadOnlyList<string>? Keys);

/// <summary>Partial update of a room: null fields are left unchanged. The rest of a room is fixed for its life.</summary>
/// <param name="Acoustics">The kind of space (see <see cref="CreateRoomCommand"/>); "outdoor" goes back to none.</param>
public sealed record UpdateRoomCommand(InWorldAvatars? InWorldAvatars = null, string? Acoustics = null);

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
/// <param name="AvatarId">The in-world avatar (NPC) the integration made for it; viewers then know it by this UUID.
/// The empty UUID clears it.</param>
public sealed record UpdateParticipantCommand(
    bool? Muted, double? Gain, Vec3? Position, Quat? Orientation, Vec3? ListenerPosition = null, Quat? ListenerOrientation = null,
    Guid? AvatarId = null);

/// <summary>One participant's new pose in a <see cref="PoseBatchCommand"/>: by our id or by its external id.</summary>
public sealed record PoseUpdate(Guid? ParticipantId, string? ExternalId, Vec3? Position, Quat? Orientation);

/// <summary>Many participants' poses at once (the simulator's avatar positions, a few times a second).</summary>
public sealed record PoseBatchCommand(IReadOnlyList<PoseUpdate>? Poses);

/// <param name="Unknown">The ids or external ids given that are not in the room (they left: stop sending them).</param>
public sealed record PoseBatchResult(int Updated, IReadOnlyList<string> Unknown);

/// <summary>A participant's change to its own pose (signalling, session token), within the room's <see cref="ClientPose"/>.</summary>
public sealed record SelfPoseCommand(Quat? ListenerOrientation, Vec3? ListenerPosition = null);

/// <summary>The caller is identified but not allowed to do this (e.g. move where the room only lets it turn).</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>Too many requests of this kind; try again shortly.</summary>
public sealed class RateLimitedException(string message) : Exception(message);

/// <summary>The server cannot take this now (it is shutting down): 503.</summary>
public sealed class ServiceUnavailableException(string message) : Exception(message);

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
