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

    void RoomCreated(RoomSnapshot room);

    void RoomDeleted(Guid roomId);

    void ParticipantAdded(ParticipantSnapshot participant);

    void ParticipantRemoved(Guid roomId, Guid participantId);

    /// <summary>Mute, gain, position or orientation changed.</summary>
    void ParticipantUpdated(ParticipantSnapshot participant);

    /// <summary>Builds the participant's endpoint from the client's SDP offer and returns the SDP answer.</summary>
    Task<string> AnswerOfferAsync(Guid roomId, Guid participantId, string offerSdp);

    void AddRemoteCandidates(Guid roomId, Guid participantId, IReadOnlyList<IceCandidate> candidates);

    Task<CandidateBatch> WaitForLocalCandidatesAsync(Guid roomId, Guid participantId, int since, TimeSpan wait, CancellationToken cancel);

    /// <summary>The room's mixing statistics (engine counters such as encoders and clusters), or null without media.</summary>
    Task<RoomMediaStats?> StatsAsync(Guid roomId);
}

/// <param name="MixedPairs">(source, listener) pairs being mixed.</param>
/// <param name="Counters">Engine-specific: e.g. encoders, talkers, clusters (matrix-shared), chains (pairchains).</param>
public sealed record RoomMediaStats(string Engine, bool Spatial, int MixedPairs, IReadOnlyDictionary<string, long> Counters);

/// <summary>Media is disabled (Media:Enabled=false): lifecycle calls do nothing, signalling is unavailable.</summary>
public sealed class NullRoomMedia : IRoomMedia
{
    public event Action<Guid, Guid, ParticipantState>? ParticipantStateChanged
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

    public Task<string> AnswerOfferAsync(Guid roomId, Guid participantId, string offerSdp) => throw new MediaUnavailableException();

    public void AddRemoteCandidates(Guid roomId, Guid participantId, IReadOnlyList<IceCandidate> candidates) =>
        throw new MediaUnavailableException();

    public Task<CandidateBatch> WaitForLocalCandidatesAsync(Guid roomId, Guid participantId, int since, TimeSpan wait, CancellationToken cancel) =>
        throw new MediaUnavailableException();

    public Task<RoomMediaStats?> StatsAsync(Guid roomId) => Task.FromResult<RoomMediaStats?>(null);
}

public sealed class MediaUnavailableException() : Exception("Media is disabled on this server (Media:Enabled=false).");
