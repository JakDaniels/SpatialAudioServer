using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media;

/// <summary>The GStreamer implementation of <see cref="IRoomMedia"/>: one <see cref="RoomPipeline"/> per room.</summary>
public sealed class GstRoomMedia(IOptions<MediaOptions> options, ILogger<GstRoomMedia> log) : IRoomMedia, IDisposable
{
    private readonly ConcurrentDictionary<Guid, RoomPipeline> _rooms = new();

    public event Action<Guid, Guid, ParticipantState>? ParticipantStateChanged;

    public void RoomCreated(RoomSnapshot room)
    {
        var pipeline = new RoomPipeline(room, options.Value, log);
        pipeline.StateChanged += (pid, state) => ParticipantStateChanged?.Invoke(room.Id, pid, state);
        if (!_rooms.TryAdd(room.Id, pipeline))
        {
            pipeline.Dispose();
        }
    }

    public void RoomDeleted(Guid roomId)
    {
        if (_rooms.TryRemove(roomId, out var pipeline))
        {
            // Teardown waits for the room's worker; keep it off the caller's (request) thread.
            _ = Task.Run(pipeline.Dispose);
        }
    }

    public void ParticipantAdded(ParticipantSnapshot participant) => Room(participant.RoomId).AddParticipant(participant);

    public void ParticipantRemoved(Guid roomId, Guid participantId)
    {
        if (_rooms.TryGetValue(roomId, out var room))
        {
            room.RemoveParticipant(participantId);
        }
    }

    public void ParticipantUpdated(ParticipantSnapshot participant) => Room(participant.RoomId).UpdateParticipant(participant);

    public Task<string> AnswerOfferAsync(Guid roomId, Guid participantId, string offerSdp) =>
        Room(roomId).AnswerOfferAsync(participantId, offerSdp);

    public void AddRemoteCandidates(Guid roomId, Guid participantId, IReadOnlyList<IceCandidate> candidates) =>
        Room(roomId).AddRemoteCandidates(participantId, candidates);

    public Task<CandidateBatch> WaitForLocalCandidatesAsync(Guid roomId, Guid participantId, int since, TimeSpan wait, CancellationToken cancel) =>
        Room(roomId).WaitForLocalCandidatesAsync(participantId, since, wait, cancel);

    /// <summary>For tests and stats: (source, listener) pairs the room's engine is mixing.</summary>
    internal Task<int> MixedPairsAsync(Guid roomId) => Room(roomId).MixedPairsAsync();

    /// <summary>For tests and stats: the room's engine-specific counters.</summary>
    internal Task<IReadOnlyDictionary<string, long>> CountersAsync(Guid roomId) => Room(roomId).CountersAsync();

    public void Dispose()
    {
        foreach (var room in _rooms.Values)
        {
            room.Dispose();
        }
        _rooms.Clear();
    }

    private RoomPipeline Room(Guid roomId) =>
        _rooms.TryGetValue(roomId, out var room) ? room : throw new NotFoundException($"Room {roomId} has no media pipeline.");
}
