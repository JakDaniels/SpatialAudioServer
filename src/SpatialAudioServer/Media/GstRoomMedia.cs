// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media;

/// <summary>
/// The GStreamer implementation of <see cref="IRoomMedia"/>: one <see cref="RoomPipeline"/> per room, running only
/// while the room has participants. The pipeline starts with the first participant and stops once the room has been
/// empty for <see cref="MediaOptions.IdleRoomTimeout"/>, so stored rooms nobody is in cost nothing.
/// </summary>
public sealed class GstRoomMedia(IOptions<MediaOptions> options, ILogger<GstRoomMedia> log) : IRoomMedia, IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, RoomEntry> _rooms = new();

    public event Action<Guid, Guid, ParticipantState>? ParticipantStateChanged;

    public event Action<Guid, Guid, string>? ParticipantData;

    public void RoomCreated(RoomSnapshot room) => _rooms.TryAdd(room.Id, new RoomEntry(room));

    public void RoomDeleted(Guid roomId)
    {
        if (!_rooms.TryRemove(roomId, out var entry))
        {
            return;
        }
        lock (entry)
        {
            entry.Deleted = true;
            Stop(entry, "room deleted");
        }
    }

    public void ParticipantAdded(ParticipantSnapshot participant)
    {
        var entry = Entry(participant.RoomId);
        lock (entry)
        {
            if (entry.Deleted)
            {
                throw new NotFoundException($"Room {participant.RoomId} has been deleted.");
            }
            entry.Participants.Add(participant.Id);
            entry.IdleGeneration++; // cancels a pending idle stop
            (entry.Pipeline ??= Start(entry)).AddParticipant(participant);
        }
    }

    public void ParticipantRemoved(Guid roomId, Guid participantId)
    {
        if (!_rooms.TryGetValue(roomId, out var entry))
        {
            return;
        }
        lock (entry)
        {
            if (!entry.Participants.Remove(participantId))
            {
                return;
            }
            entry.Pipeline?.RemoveParticipant(participantId);
            if (entry.Participants.Count == 0)
            {
                ScheduleIdleStop(entry);
            }
        }
    }

    public void ParticipantUpdated(ParticipantSnapshot participant)
    {
        if (_rooms.TryGetValue(participant.RoomId, out var entry))
        {
            lock (entry)
            {
                entry.Pipeline?.UpdateParticipant(participant);
            }
        }
    }

    public Task<string> AnswerOfferAsync(Guid roomId, Guid participantId, string offerSdp) =>
        Running(roomId).AnswerOfferAsync(participantId, offerSdp);

    public void AddRemoteCandidates(Guid roomId, Guid participantId, IReadOnlyList<IceCandidate> candidates) =>
        Running(roomId).AddRemoteCandidates(participantId, candidates);

    public Task<CandidateBatch> WaitForLocalCandidatesAsync(Guid roomId, Guid participantId, int since, TimeSpan wait, CancellationToken cancel) =>
        Running(roomId).WaitForLocalCandidatesAsync(participantId, since, wait, cancel);

    public async Task<RoomMediaStats?> StatsAsync(Guid roomId) =>
        PipelineOf(roomId, out var entry) is { } pipeline ? await pipeline.StatsAsync() : Idle(entry);

    public async Task<RoomLiveStats?> LiveStatsAsync(Guid roomId) =>
        PipelineOf(roomId, out var entry) is { } pipeline ? await pipeline.LiveStatsAsync() : new RoomLiveStats(Idle(entry), []);

    /// <summary>Null while the room's media is idle (no pipeline).</summary>
    public async Task<string?> PipelineDotAsync(Guid roomId) =>
        PipelineOf(roomId, out _) is { } pipeline ? await pipeline.PipelineDotAsync() : null;

    public async Task<PipelineGraph?> PipelineGraphAsync(Guid roomId, IReadOnlySet<string> expand) =>
        PipelineOf(roomId, out _) is { } pipeline ? await pipeline.GraphAsync(expand) : null;

    public async Task<GraphElementDetails?> ElementDetailsAsync(Guid roomId, string path) =>
        PipelineOf(roomId, out _) is { } pipeline ? await pipeline.ElementDetailsAsync(path) : null;

    /// <summary>For tests and stats: (source, listener) pairs the room's engine is mixing.</summary>
    internal async Task<int> MixedPairsAsync(Guid roomId) =>
        PipelineOf(roomId, out _) is { } pipeline ? await pipeline.MixedPairsAsync() : 0;

    /// <summary>For tests and stats: the room's engine-specific counters (none while idle).</summary>
    internal async Task<IReadOnlyDictionary<string, long>> CountersAsync(Guid roomId) =>
        PipelineOf(roomId, out _) is { } pipeline ? await pipeline.CountersAsync() : new Dictionary<string, long>();

    /// <summary>For tests: whether the room's pipeline exists.</summary>
    internal bool IsRunning(Guid roomId) => PipelineOf(roomId, out _) is not null;

    /// <summary>Stops every room's pipeline, in parallel (each waits for its own worker).</summary>
    public void Dispose()
    {
        var stopping = new List<Task>();
        foreach (var entry in _rooms.Values)
        {
            lock (entry)
            {
                entry.Deleted = true;
                if (entry.Pipeline is { } pipeline)
                {
                    entry.Pipeline = null;
                    stopping.Add(Task.Run(pipeline.Dispose));
                }
            }
        }
        _rooms.Clear();
        if (stopping.Count > 0 && !Task.WaitAll([.. stopping], ShutdownTimeout))
        {
            log.LogWarning("Not every room's media stopped within {Seconds} s", ShutdownTimeout.TotalSeconds);
        }
    }

    public void ViewerKeysGone(Guid roomId, IReadOnlyList<string> keys)
    {
        if (!_rooms.TryGetValue(roomId, out var entry))
        {
            return;
        }
        lock (entry)
        {
            foreach (var key in keys)
            {
                entry.Departures.Add(key);
            }
            entry.Pipeline?.ViewerKeysGone(keys);
        }
    }

    private RoomPipeline Start(RoomEntry entry)
    {
        var room = entry.Room;
        var pipeline = new RoomPipeline(room, options.Value, log, entry.Departures);
        pipeline.StateChanged += (pid, state) => ParticipantStateChanged?.Invoke(room.Id, pid, state);
        pipeline.DataReceived += (pid, text) => ParticipantData?.Invoke(room.Id, pid, text);
        log.LogInformation("Room {RoomId}: media started", room.Id);
        return pipeline;
    }

    /// <summary>Under the entry's lock.</summary>
    private void ScheduleIdleStop(RoomEntry entry)
    {
        var generation = ++entry.IdleGeneration;
        var timeout = options.Value.IdleRoomTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            Stop(entry, "room empty");
            return;
        }
        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            lock (entry)
            {
                if (generation == entry.IdleGeneration && entry.Participants.Count == 0)
                {
                    Stop(entry, $"room empty for {timeout.TotalSeconds:F0} s");
                }
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Under the entry's lock. Teardown waits for the room's worker, so it runs off the caller's thread.</summary>
    private void Stop(RoomEntry entry, string reason)
    {
        if (entry.Pipeline is not { } pipeline)
        {
            return;
        }
        entry.Pipeline = null;
        log.LogInformation("Room {RoomId}: media stopped ({Reason})", entry.Room.Id, reason);
        _ = Task.Run(pipeline.Dispose);
    }

    private RoomEntry Entry(Guid roomId) =>
        _rooms.TryGetValue(roomId, out var entry) ? entry : throw new NotFoundException($"Room {roomId} has no media.");

    private RoomPipeline? PipelineOf(Guid roomId, out RoomEntry entry)
    {
        entry = Entry(roomId);
        lock (entry)
        {
            return entry.Pipeline;
        }
    }

    private RoomPipeline Running(Guid roomId) =>
        PipelineOf(roomId, out _) ?? throw new NotFoundException($"Room {roomId} has no participants, so no media is running.");

    private static RoomMediaStats Idle(RoomEntry entry) =>
        new(entry.Room.Engine, entry.Room.Spatial, 0, new Dictionary<string, long>(), Running: false);

    /// <summary>A room's media state; fields are guarded by locking the entry.</summary>
    private sealed class RoomEntry(RoomSnapshot room)
    {
        public RoomSnapshot Room { get; } = room;
        public RoomPipeline? Pipeline { get; set; }
        public HashSet<Guid> Participants { get; } = [];
        /// <summary>Recent leavers, replayed to SL viewers that join; kept while the media is stopped.</summary>
        public ViewerDepartures Departures { get; } = new();
        /// <summary>Bumped by every add and every idle-stop schedule, so a stale idle timer does nothing.</summary>
        public int IdleGeneration { get; set; }
        public bool Deleted { get; set; }
    }
}
