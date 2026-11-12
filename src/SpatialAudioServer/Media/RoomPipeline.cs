using System.Collections.Concurrent;
using Gst;
using Gst.WebRTC;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Media.Engines;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media;

/// <summary>
/// One GStreamer pipeline per room. This class owns each participant's webrtcbin endpoint (offer/answer, ICE,
/// connection state) and the order of teardown; the room's <see cref="IMixEngine"/> owns everything between a
/// participant's incoming RTP and the RTP sent back to them.
/// </summary>
/// <remarks>
/// Every graph change runs on the room's <see cref="RoomWorker"/>, except handing a newly announced incoming pad to
/// the engine, which must happen synchronously in pad-added (the streaming thread pushes as soon as the handler returns).
/// </remarks>
internal sealed class RoomPipeline : IDisposable
{
    private readonly Pipeline _pipeline;
    private readonly RoomWorker _worker;
    private readonly MediaOptions _options;
    private readonly ILogger _log;
    private readonly IMixEngine _engine;
    private readonly ConcurrentDictionary<Guid, ParticipantMedia> _participants = new();
    private readonly BusWatcher _bus;
    private int _disposed;

    public RoomPipeline(RoomSnapshot room, MediaOptions options, ILogger log)
    {
        var roomId = room.Id;
        RoomId = roomId;
        EngineName = room.Engine;
        _options = options;
        _log = log;
        _pipeline = Pipeline.New($"room-{roomId:N}");
        _worker = new RoomWorker($"room-{roomId.ToString()[..8]}", log);
        if (_pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
        {
            throw new InvalidOperationException($"Room {roomId} pipeline refused to go to PLAYING.");
        }
        _bus = new BusWatcher(_pipeline, $"room-{roomId.ToString()[..8]}", log);
        _engine = MixEngines.Create(room.Engine, new MixEngineContext(roomId, room.MaxParticipants, _pipeline, _worker, options, log));
    }

    public Guid RoomId { get; }

    public string EngineName { get; }

    /// <summary>Raised on webrtcbin's thread when a participant's connection state changes.</summary>
    public event Action<Guid, ParticipantState>? StateChanged;

    /// <summary>(source, listener) pairs the engine is mixing. For tests and stats.</summary>
    public Task<int> MixedPairsAsync() => _worker.InvokeAsync(() => _engine.MixedPairs);

    public Task<IReadOnlyDictionary<string, long>> CountersAsync() => _worker.InvokeAsync(() => _engine.Counters);

    public void AddParticipant(ParticipantSnapshot participant) => _worker.Post(() =>
    {
        if (_participants.TryAdd(participant.Id, new ParticipantMedia(participant.Id)))
        {
            _engine.ParticipantAdded(participant);
        }
    });

    public void UpdateParticipant(ParticipantSnapshot participant) => _worker.Post(() =>
    {
        if (_participants.ContainsKey(participant.Id))
        {
            _engine.ParticipantUpdated(participant);
        }
    });

    public void RemoveParticipant(Guid participantId) => _worker.Post(() =>
    {
        if (_participants.TryRemove(participantId, out var p))
        {
            RemoveEndpoint(p);
            _engine.ParticipantRemoved(participantId);
            _log.LogInformation("Room {RoomId}: participant {ParticipantId} media removed", RoomId, participantId);
        }
    });

    /// <summary>Builds (or rebuilds) the participant's endpoint from the browser's offer and returns the answer.</summary>
    public Task<string> AnswerOfferAsync(Guid participantId, string offerSdp) => _worker.InvokeAsync(() =>
    {
        if (!_participants.TryGetValue(participantId, out var p))
        {
            throw new NotFoundException($"Participant {participantId} has no media in room {RoomId}.");
        }
        // The send branch payloads with the offer's Opus payload type (Chrome 111, Firefox 109).
        var payloadType = WebRtcBin.OpusPayloadType(offerSdp)
            ?? throw new ArgumentException("The offer has no Opus audio.");
        var mline = WebRtcBin.FirstAudioMLine(offerSdp)
            ?? throw new ArgumentException("The offer has no audio m-line.");

        if (p.Endpoint is not null)
        {
            _log.LogInformation("Room {RoomId}: participant {ParticipantId} re-offered; rebuilding its endpoint", RoomId, participantId);
            RemoveEndpoint(p);
        }

        var endpoint = BuildEndpoint(p);
        p.Endpoint = endpoint;
        try
        {
            // Deterministic answerer order (see WebRtcBin.PrepareSendTransceiver): offer first, then bind the send
            // branch to the transceiver the offer created, by requesting that m-line's sink pad explicitly.
            endpoint.Rtc.ApplyRemoteOffer(offerSdp);
            using (var codec = Caps.FromString(MediaBlocks.SendCaps(payloadType)) ?? throw new InvalidOperationException("Bad send caps."))
            {
                endpoint.Rtc.PrepareSendTransceiver(mline, codec);
            }
            var rtcSink = endpoint.Rtc.Element.RequestPadSimple($"sink_{mline}")
                ?? throw new InvalidOperationException($"webrtcbin gave no sink_{mline} pad.");
            endpoint.ListenerAttached = true;
            _engine.AttachListener(p.Id, payloadType, rtcSink);
            return endpoint.Rtc.CreateAndApplyAnswer();
        }
        catch
        {
            RemoveEndpoint(p);
            throw;
        }
    });

    public void AddRemoteCandidates(Guid participantId, IEnumerable<IceCandidate> candidates)
    {
        var rtc = RtcOf(participantId);
        foreach (var candidate in candidates)
        {
            rtc.AddRemoteCandidate(candidate);
        }
    }

    public Task<CandidateBatch> WaitForLocalCandidatesAsync(Guid participantId, int since, TimeSpan wait, CancellationToken cancel) =>
        RtcOf(participantId).WaitForLocalCandidatesAsync(since, wait, cancel);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            _worker.InvokeAsync(() =>
            {
                foreach (var p in _participants.Values)
                {
                    RemoveEndpoint(p);
                    _engine.ParticipantRemoved(p.Id);
                }
                _participants.Clear();
                _engine.Dispose();
                _pipeline.SetState(State.Null);
                return true;
            }).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Room {RoomId}: teardown failed", RoomId);
        }
        _worker.Dispose();
        _bus.Dispose();
        _pipeline.Dispose();
    }

    private WebRtcBin RtcOf(Guid participantId) =>
        _participants.TryGetValue(participantId, out var p) && p.Endpoint is { } endpoint
            ? endpoint.Rtc
            : throw new ConflictException($"Participant {participantId} has not sent an offer yet.");

    private Endpoint BuildEndpoint(ParticipantMedia p)
    {
        var rtc = new WebRtcBin($"webrtc-{MediaBlocks.ShortId(p.Id)}", _options.JitterBufferMs);
        MediaBlocks.Add(_pipeline, rtc.Element);
        var endpoint = new Endpoint(rtc);
        rtc.IncomingPad += pad => OnIncomingPad(p, endpoint, pad);
        rtc.ConnectionStateChanged += state =>
        {
            if (state == WebRTCPeerConnectionState.Failed)
            {
                _log.LogWarning("Room {RoomId}: participant {ParticipantId} connection failed: {Diagnostics}",
                    RoomId, p.Id, rtc.Diagnostics());
            }
            StateChanged?.Invoke(p.Id, Map(state));
        };
        // Only webrtcbin starts now; the engine starts its send branch once it is linked to the negotiated transceiver.
        rtc.Element.SyncStateWithParent();
        return endpoint;
    }

    /// <summary>Streaming thread: hands the participant's incoming audio to the engine.</summary>
    private void OnIncomingPad(ParticipantMedia p, Endpoint endpoint, Pad pad)
    {
        lock (endpoint.Sync)
        {
            if (endpoint.Removed || endpoint.SourceAttached)
            {
                return;
            }
            endpoint.SourceAttached = true;
            _engine.AttachSource(p.Id, pad);
        }
        _log.LogInformation("Room {RoomId}: participant {ParticipantId} audio is arriving", RoomId, p.Id);
    }

    /// <summary>
    /// Worker: the engine takes the participant out of every mix while everything runs, then webrtcbin stops (which
    /// joins its threads, so no callback runs after), then the engine releases the participant's own elements.
    /// </summary>
    private void RemoveEndpoint(ParticipantMedia p)
    {
        if (p.Endpoint is not { } endpoint)
        {
            return;
        }
        lock (endpoint.Sync)
        {
            endpoint.Removed = true; // no source can be attached from here on
        }
        if (endpoint.ListenerAttached || endpoint.SourceAttached)
        {
            _engine.Disconnect(p.Id);
        }
        endpoint.Rtc.Dispose();
        endpoint.Rtc.Element.SetState(State.Null);
        if (endpoint.ListenerAttached || endpoint.SourceAttached)
        {
            _engine.Release(p.Id);
        }
        _pipeline.Remove(endpoint.Rtc.Element);
        p.Endpoint = null;
    }

    private static ParticipantState Map(WebRTCPeerConnectionState state) => state switch
    {
        WebRTCPeerConnectionState.Connected => ParticipantState.Connected,
        WebRTCPeerConnectionState.Failed => ParticipantState.Failed,
        WebRTCPeerConnectionState.Disconnected or WebRTCPeerConnectionState.Closed => ParticipantState.Disconnected,
        _ => ParticipantState.Connecting,
    };

    /// <summary>What the room knows about a participant's media. Mutated on the worker only.</summary>
    private sealed class ParticipantMedia(Guid id)
    {
        public Guid Id { get; } = id;
        public Endpoint? Endpoint { get; set; }
    }

    /// <summary>A participant's webrtcbin once they have offered.</summary>
    private sealed class Endpoint(WebRtcBin rtc)
    {
        public object Sync { get; } = new();
        public WebRtcBin Rtc { get; } = rtc;
        public bool ListenerAttached { get; set; }
        public bool SourceAttached { get; set; } // under Sync
        public bool Removed { get; set; } // under Sync
    }
}
