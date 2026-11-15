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
    private readonly ViewerData _viewers;
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
        Spatial = room.Spatial;
        _engine = MixEngines.Create(room.Engine, new MixEngineContext(roomId, room.MaxParticipants, _pipeline, _worker, options, log, room.Spatial));
        _viewers = new ViewerData(_worker, options, () => _engine.InputLevels,
            id => _participants.TryGetValue(id, out var p) ? p.Endpoint?.Rtc : null);
    }

    public Guid RoomId { get; }

    public string EngineName { get; }

    public bool Spatial { get; }

    /// <summary>Raised on webrtcbin's thread when a participant's connection state changes.</summary>
    public event Action<Guid, ParticipantState>? StateChanged;

    /// <summary>Raised on webrtcbin's SCTP thread for each text message on a participant's data channel.</summary>
    public event Action<Guid, string>? DataReceived;

    /// <summary>(source, listener) pairs the engine is mixing. For tests and stats.</summary>
    public Task<int> MixedPairsAsync() => _worker.InvokeAsync(() => _engine.MixedPairs);

    public Task<IReadOnlyDictionary<string, long>> CountersAsync() => _worker.InvokeAsync(() => _engine.Counters);

    public Task<RoomMediaStats> StatsAsync() =>
        _worker.InvokeAsync(() => new RoomMediaStats(EngineName, Spatial, _engine.MixedPairs, _engine.Counters));

    /// <summary>
    /// Engine stats plus, per participant, the input level and the connection's statistics. The get-stats requests
    /// go out on the worker (endpoints cannot be removed meanwhile); the replies are awaited off it.
    /// </summary>
    public async Task<RoomLiveStats> LiveStatsAsync()
    {
        var (media, levels, requests) = await _worker.InvokeAsync(() => (
            new RoomMediaStats(EngineName, Spatial, _engine.MixedPairs, _engine.Counters),
            _engine.InputLevels,
            _participants.Values.Select(p => (p.Id, Request: p.Endpoint is { } e ? TryRequestStats(e.Rtc) : null)).ToList()));
        var participants = await System.Threading.Tasks.Task.WhenAll(requests.Select(async r => new ParticipantLiveStats(
            r.Id,
            levels.TryGetValue(r.Id, out var rms) ? Math.Round(20 * Math.Log10(Math.Max(rms, 1e-5)), 1) : null,
            r.Request is null ? null : await r.Request.ResultAsync(TimeSpan.FromSeconds(2)))));
        return new RoomLiveStats(media, participants);
    }

    private WebRtcBin.StatsRequest? TryRequestStats(WebRtcBin rtc)
    {
        try
        {
            return rtc.RequestStats();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Room {RoomId}: get-stats failed", RoomId);
            return null;
        }
    }

    /// <summary>The room's pipeline in Graphviz form (GStreamer's debug dump), taken on the worker.</summary>
    public Task<string> PipelineDotAsync() =>
        _worker.InvokeAsync(() => Global.DebugBinToDotData(_pipeline, DebugGraphDetails.All));

    /// <summary>A snapshot for the admin pipeline view (bins in <paramref name="expand"/> with their children), taken on the worker.</summary>
    public Task<PipelineGraph> GraphAsync(IReadOnlySet<string> expand) =>
        _worker.InvokeAsync(() => PipelineGraphReader.Read(_pipeline, EngineName, _participants.Keys.ToList(), RoomId, expand));

    /// <summary>One element's properties, or null if there is no element at that path.</summary>
    public Task<GraphElementDetails?> ElementDetailsAsync(string path) =>
        _worker.InvokeAsync(() => PipelineGraphReader.Details(_pipeline, path));

    public void AddParticipant(ParticipantSnapshot participant) => _worker.Post(() =>
    {
        if (_participants.TryAdd(participant.Id, new ParticipantMedia(participant.Id)))
        {
            _engine.ParticipantAdded(participant);
            _viewers.Added(participant);
        }
    });

    public void UpdateParticipant(ParticipantSnapshot participant) => _worker.Post(() =>
    {
        if (_participants.ContainsKey(participant.Id))
        {
            _engine.ParticipantUpdated(participant);
            _viewers.Updated(participant);
        }
    });

    public void RemoveParticipant(Guid participantId) => _worker.Post(() =>
    {
        if (_participants.TryRemove(participantId, out var p))
        {
            RemoveEndpoint(p);
            _engine.ParticipantRemoved(participantId);
            _viewers.Removed(participantId);
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
        _viewers.Dispose();
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
        rtc.ConfigureIceServers(_options.IceServers);
        MediaBlocks.Add(_pipeline, rtc.Element);
        var endpoint = new Endpoint(rtc);
        rtc.IncomingPad += pad => OnIncomingPad(p, endpoint, pad);
        rtc.ConnectionStateChanged += state =>
        {
            if (endpoint.Removed)
            {
                return; // a replaced endpoint (re-offer) must not report over its successor, e.g. a late "closed"
            }
            if (state == WebRTCPeerConnectionState.Failed)
            {
                _log.LogWarning("Room {RoomId}: participant {ParticipantId} connection failed: {Diagnostics}",
                    RoomId, p.Id, rtc.Diagnostics());
            }
            if (state == WebRTCPeerConnectionState.Connected)
            {
                _worker.Post(() =>
                {
                    if (!endpoint.Removed)
                    {
                        _viewers.Connected(p.Id);
                    }
                });
            }
            StateChanged?.Invoke(p.Id, Map(state));
        };
        rtc.DataChannelOpened += () => _log.LogInformation("Room {RoomId}: participant {ParticipantId} data channel '{Label}' is open",
            RoomId, p.Id, rtc.DataChannelLabel);
        rtc.DataMessage += text =>
        {
            if (endpoint.Removed)
            {
                return;
            }
            if (text.Contains("\"j\"", StringComparison.Ordinal) && SlData.Parse(text) is { Joined: true } join)
            {
                _worker.Post(() =>
                {
                    if (!endpoint.Removed)
                    {
                        _viewers.ViewerJoined(p.Id, join.Primary);
                    }
                });
            }
            DataReceived?.Invoke(p.Id, text);
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
        _viewers.EndpointRemoved(p.Id);
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
