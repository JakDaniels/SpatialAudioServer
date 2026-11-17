// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media;

/// <summary>
/// The boundary between the room domain and the GStreamer layer. <see cref="RoomManager"/> calls the lifecycle
/// methods after a change is committed, outside its lock; they must not block. The signalling endpoints call the
/// offer/candidate methods. <see cref="NullRoomMedia"/> stands in when media is disabled (tests without GStreamer).
/// </summary>
public interface IRoomMedia
{
    /// <summary>Raised (on a GStreamer thread) when a participant's WebRTC connection state changes.</summary>
    event Action<Guid, Guid, ParticipantState>? ParticipantStateChanged;

    /// <summary>Raised (on a GStreamer thread) for each text message on a participant's data channel (an SL viewer's SLData).</summary>
    event Action<Guid, Guid, string>? ParticipantData;

    void RoomCreated(RoomSnapshot room);

    void RoomDeleted(Guid roomId);

    void ParticipantAdded(ParticipantSnapshot participant);

    void ParticipantRemoved(Guid roomId, Guid participantId);

    /// <summary>Mute, gain, position or orientation changed.</summary>
    void ParticipantUpdated(ParticipantSnapshot participant);

    /// <summary>The integration reports these viewer keys gone from the room: SL viewers drop them from their lists.</summary>
    void ViewerKeysGone(Guid roomId, IReadOnlyList<string> keys);

    /// <summary>Builds the participant's endpoint from the client's SDP offer and returns the SDP answer.</summary>
    Task<string> AnswerOfferAsync(Guid roomId, Guid participantId, string offerSdp);

    void AddRemoteCandidates(Guid roomId, Guid participantId, IReadOnlyList<IceCandidate> candidates);

    Task<CandidateBatch> WaitForLocalCandidatesAsync(Guid roomId, Guid participantId, int since, TimeSpan wait, CancellationToken cancel);

    /// <summary>The room's mixing statistics (engine counters such as encoders and clusters), or null without media.</summary>
    Task<RoomMediaStats?> StatsAsync(Guid roomId);

    /// <summary>Engine statistics plus each participant's input level and connection statistics; null without media.</summary>
    Task<RoomLiveStats?> LiveStatsAsync(Guid roomId);

    /// <summary>The room's GStreamer pipeline as Graphviz .dot text; null without media or while the room's media is idle.</summary>
    Task<string?> PipelineDotAsync(Guid roomId);

    /// <summary>The room's pipeline for the admin view (bins in <paramref name="expand"/> with their children); null without
    /// media or while idle.</summary>
    Task<PipelineGraph?> PipelineGraphAsync(Guid roomId, IReadOnlySet<string> expand);

    /// <summary>One element's properties; null without media, while idle, or if no element has that path.</summary>
    Task<GraphElementDetails?> ElementDetailsAsync(Guid roomId, string path);
}

/// <param name="LevelDb">Incoming audio level, dBFS (peak-held RMS), or null when the engine does not measure it or no
/// audio has arrived.</param>
/// <param name="Connection">webrtcbin's statistics, or null before the client has offered or if they did not come in time.</param>
public sealed record ParticipantLiveStats(Guid Id, double? LevelDb, ConnectionStats? Connection);

public sealed record RoomLiveStats(RoomMediaStats Media, IReadOnlyList<ParticipantLiveStats> Participants);

/// <param name="MixedPairs">(source, listener) pairs being mixed.</param>
/// <param name="Counters">Engine-specific: e.g. encoders, talkers, clusters (matrix-shared), chains (pairchains).</param>
/// <param name="Running">The room's pipeline exists. It runs only while the room has participants (and for
/// Media:IdleRoomTimeout after the last one leaves).</param>
public sealed record RoomMediaStats(string Engine, bool Spatial, int MixedPairs, IReadOnlyDictionary<string, long> Counters, bool Running = true);

/// <summary>Media is disabled (Media:Enabled=false): lifecycle calls do nothing, signalling is unavailable.</summary>
public sealed class NullRoomMedia : IRoomMedia
{
    public event Action<Guid, Guid, ParticipantState>? ParticipantStateChanged
    {
        add { }
        remove { }
    }

    public event Action<Guid, Guid, string>? ParticipantData
    {
        add { }
        remove { }
    }

    public void RoomCreated(RoomSnapshot room)
    {
    }

    public void RoomDeleted(Guid roomId)
    {
    }

    public void ParticipantAdded(ParticipantSnapshot participant)
    {
    }

    public void ParticipantRemoved(Guid roomId, Guid participantId)
    {
    }

    public void ParticipantUpdated(ParticipantSnapshot participant)
    {
    }

    public void ViewerKeysGone(Guid roomId, IReadOnlyList<string> keys)
    {
    }

    public Task<string> AnswerOfferAsync(Guid roomId, Guid participantId, string offerSdp) => throw new MediaUnavailableException();

    public void AddRemoteCandidates(Guid roomId, Guid participantId, IReadOnlyList<IceCandidate> candidates) =>
        throw new MediaUnavailableException();

    public Task<CandidateBatch> WaitForLocalCandidatesAsync(Guid roomId, Guid participantId, int since, TimeSpan wait, CancellationToken cancel) =>
        throw new MediaUnavailableException();

    public Task<RoomMediaStats?> StatsAsync(Guid roomId) => Task.FromResult<RoomMediaStats?>(null);

    public Task<RoomLiveStats?> LiveStatsAsync(Guid roomId) => Task.FromResult<RoomLiveStats?>(null);

    public Task<string?> PipelineDotAsync(Guid roomId) => Task.FromResult<string?>(null);

    public Task<PipelineGraph?> PipelineGraphAsync(Guid roomId, IReadOnlySet<string> expand) => Task.FromResult<PipelineGraph?>(null);

    public Task<GraphElementDetails?> ElementDetailsAsync(Guid roomId, string path) => Task.FromResult<GraphElementDetails?>(null);
}

public sealed class MediaUnavailableException() : Exception("Media is disabled on this server (Media:Enabled=false).");
