using System.Collections.Concurrent;
using System.Globalization;
using Gst;
using Gst.GLib;
using Gst.WebRTC;
using DateTime = System.DateTime;
using SpatialAudioServer.Configuration;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Media;

/// <summary>
/// One GStreamer pipeline per room, holding every participant's endpoint and the per-(source, listener) mix chains:
/// <code>
/// ingress (per participant):  webrtcbin src → queue → rtpopusdepay → opusdec → convert/resample (mono 48k) → tee
/// chain (per source≠listener): source tee → queue (leaky) → audioamplify (gain) → audiopanorama (pan) → listener mixer
/// egress (per participant):   silence + chains → audiomixer → S16 stereo 48k → opusenc → rtpopuspay → webrtcbin sink
/// </code>
/// </summary>
/// <remarks>
/// Every graph change runs on the room's <see cref="RoomWorker"/>, except linking a newly announced incoming
/// pad, which must happen synchronously in pad-added (the streaming thread pushes as soon as the handler returns).
/// </remarks>
internal sealed class RoomPipeline : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly Pipeline _pipeline;
    private readonly RoomWorker _worker;
    private readonly MediaOptions _options;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<Guid, ParticipantMedia> _participants = new();
    private readonly Dictionary<(Guid Source, Guid Listener), MixChain> _chains = []; // worker only
    private readonly BusWatcher _bus;
    private int _disposed;

    public RoomPipeline(Guid roomId, MediaOptions options, ILogger log)
    {
        RoomId = roomId;
        _options = options;
        _log = log;
        _pipeline = Pipeline.New($"room-{roomId:N}");
        _worker = new RoomWorker($"room-{roomId.ToString()[..8]}", log);
        if (_pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
        {
            throw new InvalidOperationException($"Room {roomId} pipeline refused to go to PLAYING.");
        }
        _bus = new BusWatcher(_pipeline, $"room-{roomId.ToString()[..8]}", log);
    }

    public Guid RoomId { get; }

    /// <summary>Raised on webrtcbin's thread when a participant's connection state changes.</summary>
    public event Action<Guid, ParticipantState>? StateChanged;

    /// <summary>Number of live (source, listener) chains. Worker-owned; for tests and stats.</summary>
    public Task<int> ChainCountAsync() => _worker.InvokeAsync(() => _chains.Count);

    public void AddParticipant(ParticipantSnapshot participant) =>
        _worker.Post(() => _participants.TryAdd(participant.Id, new ParticipantMedia(participant.Id, participant.Gain, participant.Muted)));

    public void UpdateParticipant(ParticipantSnapshot participant) => _worker.Post(() =>
    {
        if (!_participants.TryGetValue(participant.Id, out var p))
        {
            return;
        }
        p.Gain = participant.Gain;
        p.Muted = participant.Muted;
        foreach (var chain in _chains.Values.Where(c => c.Source == participant.Id))
        {
            chain.SetGain(SourceGain(p));
        }
    });

    public void RemoveParticipant(Guid participantId) => _worker.Post(() =>
    {
        if (_participants.TryRemove(participantId, out var p))
        {
            RemoveEndpoint(p);
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

        var endpoint = BuildEndpoint(p, payloadType);
        p.Endpoint = endpoint;
        string answer;
        try
        {
            // Deterministic answerer order (see WebRtcBin.PrepareSendTransceiver): offer first, then bind the send
            // branch to the transceiver the offer created, by requesting that m-line's sink pad explicitly.
            endpoint.Rtc.ApplyRemoteOffer(offerSdp);
            using (var codec = Caps.FromString(SendCaps(payloadType)) ?? throw new InvalidOperationException("Bad send caps."))
            {
                endpoint.Rtc.PrepareSendTransceiver(mline, codec);
            }
            Link(endpoint.Egress.GetStaticPad("src"), endpoint.Rtc.Element.RequestPadSimple($"sink_{mline}"), "egress → webrtcbin");
            // Downstream first, so nothing pushes into an element that is not running yet.
            endpoint.Egress.SyncStateWithParent();
            endpoint.Mixer.SyncStateWithParent();
            endpoint.Silence.SyncStateWithParent();
            answer = endpoint.Rtc.CreateAndApplyAnswer();
        }
        catch
        {
            RemoveEndpoint(p);
            throw;
        }

        // Everyone already sending is mixed into the newcomer's ear.
        foreach (var source in _participants.Values)
        {
            EnsureChain(source, p);
        }
        return answer;
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
                }
                _participants.Clear();
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

    private static float SourceGain(ParticipantMedia p) => p.Muted ? 0f : (float)p.Gain;

    private static string SendCaps(int payloadType) =>
        $"application/x-rtp,media=audio,encoding-name=OPUS,payload={payloadType},clock-rate=48000";

    private Endpoint BuildEndpoint(ParticipantMedia p, int payloadType)
    {
        var id = p.Id.ToString("N")[..8];
        var rtc = new WebRtcBin($"webrtc-{id}", _options.JitterBufferMs);

        var mixer = ElementFactory.Make("audiomixer", $"mix-{id}") ?? throw new InvalidOperationException("audiomixer missing");
        mixer.SetProperty("latency", (ulong)_options.MixerLatencyMs * 1_000_000);

        // Keeps the mixer (and so the outgoing Opus stream) running when nobody else is talking or the listener is alone.
        var silence = ElementFactory.Make("audiotestsrc", $"silence-{id}") ?? throw new InvalidOperationException("audiotestsrc missing");
        silence.SetProperty("is-live", true);
        Global.UtilSetObjectArg(silence, "wave", "silence");

        var egress = Global.ParseBinFromDescription(
            "capsfilter caps=\"audio/x-raw,format=S16LE,rate=48000,channels=2\" ! " +
            $"opusenc bitrate={_options.OpusBitrate.ToString(CultureInfo.InvariantCulture)} inband-fec=true ! " +
            $"rtpopuspay pt={payloadType} ! " +
            $"capsfilter caps=\"{SendCaps(payloadType)}\"",
            ghostUnlinkedPads: true);
        egress.SetName($"egress-{id}");

        foreach (var element in new Element[] { rtc.Element, mixer, silence, egress })
        {
            if (!_pipeline.Add(element))
            {
                throw new InvalidOperationException($"Could not add {element.Name} to the room pipeline.");
            }
        }

        var endpoint = new Endpoint(rtc, mixer, silence, egress);
        rtc.IncomingPad += pad => OnIncomingPad(p, endpoint, pad);
        rtc.ConnectionStateChanged += state => StateChanged?.Invoke(p.Id, Map(state));

        Link(silence.GetStaticPad("src"), mixer.RequestPadSimple("sink_%u"), "silence → mixer");
        Link(mixer.GetStaticPad("src"), egress.GetStaticPad("sink"), "mixer → egress");

        // Only webrtcbin starts now; the send branch starts once it is linked to the negotiated transceiver.
        rtc.Element.SyncStateWithParent();
        return endpoint;
    }

    /// <summary>Streaming thread: builds and links the ingress for the participant's incoming audio.</summary>
    private void OnIncomingPad(ParticipantMedia p, Endpoint endpoint, Pad pad)
    {
        lock (endpoint.Sync)
        {
            if (endpoint.Removed || endpoint.Tee is not null)
            {
                return;
            }
            var id = p.Id.ToString("N")[..8];
            var ingress = Global.ParseBinFromDescription(
                "queue ! rtpopusdepay ! opusdec plc=true ! audioconvert ! audioresample ! " +
                "capsfilter caps=\"audio/x-raw,rate=48000,channels=1\"",
                ghostUnlinkedPads: true);
            ingress.SetName($"ingress-{id}");
            var tee = ElementFactory.Make("tee", $"tee-{id}") ?? throw new InvalidOperationException("tee missing");
            tee.SetProperty("allow-not-linked", true);

            _pipeline.Add(tee);
            _pipeline.Add(ingress);
            Link(ingress.GetStaticPad("src"), tee.GetStaticPad("sink"), "ingress → tee");
            tee.SyncStateWithParent();
            ingress.SyncStateWithParent();
            Link(pad, ingress.GetStaticPad("sink"), "webrtcbin → ingress");

            endpoint.Ingress = ingress;
            endpoint.Tee = tee;
        }
        _log.LogInformation("Room {RoomId}: participant {ParticipantId} audio is arriving", RoomId, p.Id);

        // Now mix this participant into everyone else's ear.
        _worker.Post(() =>
        {
            if (p.Endpoint != endpoint || endpoint.Removed)
            {
                return;
            }
            foreach (var listener in _participants.Values)
            {
                EnsureChain(p, listener);
            }
        });
    }

    /// <summary>Worker: adds the source→listener chain if both ends exist and it is not there yet.</summary>
    private void EnsureChain(ParticipantMedia source, ParticipantMedia listener)
    {
        if (source.Id == listener.Id // mix-minus: nobody hears themselves
            || _chains.ContainsKey((source.Id, listener.Id))
            || source.Endpoint?.Tee is not { } tee
            || listener.Endpoint?.Mixer is not { } mixer)
        {
            return;
        }

        var bin = Global.ParseBinFromDescription(
            $"queue max-size-buffers=0 max-size-bytes=0 max-size-time={(ulong)_options.ChainQueueMs * 1_000_000} leaky=downstream ! " +
            "audioamplify name=amp ! audiopanorama name=pan",
            ghostUnlinkedPads: true);
        bin.SetName($"chain-{source.Id.ToString("N")[..8]}-{listener.Id.ToString("N")[..8]}");
        var chain = new MixChain(source.Id, listener.Id, bin, tee, mixer);
        chain.SetGain(SourceGain(source));
        chain.SetPan(0f); // centre until the spatial policy (Phase 6) supplies one

        _pipeline.Add(bin);
        chain.MixerPad = mixer.RequestPadSimple("sink_%u") ?? throw new InvalidOperationException("No mixer pad.");
        Link(bin.GetStaticPad("src"), chain.MixerPad, "chain → mixer");
        bin.SyncStateWithParent();
        chain.TeePad = tee.RequestPadSimple("src_%u") ?? throw new InvalidOperationException("No tee pad.");
        Link(chain.TeePad, bin.GetStaticPad("sink"), "tee → chain");

        _chains[(source.Id, listener.Id)] = chain;
        _log.LogDebug("Room {RoomId}: chain {Source} → {Listener} added", RoomId, source.Id, listener.Id);
    }

    /// <summary>
    /// Worker: removes a chain while the pipeline plays. The tee pad is unlinked from an IDLE probe (so no buffer is
    /// mid-push), then the chain is shut down and both request pads are released.
    /// </summary>
    private void RemoveChain(MixChain chain)
    {
        _chains.Remove((chain.Source, chain.Listener));
        var chainSink = chain.Bin.GetStaticPad("sink");
        if (chain.TeePad is { } teePad && chainSink is not null)
        {
            var unlinked = new ManualResetEventSlim();
            teePad.AddProbe(PadProbeType.Idle, (pad, _) =>
            {
                pad.Unlink(chainSink);
                unlinked.Set();
                return PadProbeReturn.Remove;
            });
            if (!unlinked.Wait(ProbeTimeout))
            {
                _log.LogWarning("Room {RoomId}: idle probe timed out removing chain {Name}; unlinking anyway", RoomId, chain.Bin.Name);
                teePad.Unlink(chainSink);
            }
            chain.Tee.ReleaseRequestPad(teePad);
        }

        chain.Bin.SetState(State.Null);
        if (chain.MixerPad is { } mixerPad)
        {
            chain.Bin.GetStaticPad("src")?.Unlink(mixerPad);
            chain.Mixer.ReleaseRequestPad(mixerPad);
        }
        _pipeline.Remove(chain.Bin);
        _log.LogDebug("Room {RoomId}: chain {Source} → {Listener} removed", RoomId, chain.Source, chain.Listener);
    }

    /// <summary>Worker: removes every chain touching the participant, then its endpoint's elements.</summary>
    private void RemoveEndpoint(ParticipantMedia p)
    {
        if (p.Endpoint is not { } endpoint)
        {
            return;
        }
        foreach (var chain in _chains.Values.Where(c => c.Source == p.Id || c.Listener == p.Id).ToList())
        {
            RemoveChain(chain);
        }

        lock (endpoint.Sync)
        {
            endpoint.Removed = true; // no ingress can be built from here on
        }
        endpoint.Rtc.Dispose();

        // webrtcbin first: going to NULL joins its threads, so no callback runs after this.
        var elements = new List<Element> { endpoint.Rtc.Element, endpoint.Silence, endpoint.Mixer, endpoint.Egress };
        if (endpoint.Ingress is { } ingress)
        {
            elements.Add(ingress);
        }
        if (endpoint.Tee is { } tee)
        {
            elements.Add(tee);
        }
        foreach (var element in elements)
        {
            element.SetState(State.Null);
        }
        foreach (var element in elements)
        {
            _pipeline.Remove(element);
        }
        p.Endpoint = null;
    }

    private static void Link(Pad? source, Pad? sink, string what)
    {
        if (source is null || sink is null)
        {
            throw new InvalidOperationException($"Linking {what}: missing pad.");
        }
        var result = source.Link(sink);
        if (result != PadLinkReturn.Ok)
        {
            throw new InvalidOperationException($"Linking {what}: {result}.");
        }
    }

    private static ParticipantState Map(WebRTCPeerConnectionState state) => state switch
    {
        WebRTCPeerConnectionState.Connected => ParticipantState.Connected,
        WebRTCPeerConnectionState.Failed => ParticipantState.Failed,
        WebRTCPeerConnectionState.Disconnected or WebRTCPeerConnectionState.Closed => ParticipantState.Disconnected,
        _ => ParticipantState.Connecting,
    };

    /// <summary>What the media layer knows about a participant. Mutated on the worker only.</summary>
    private sealed class ParticipantMedia(Guid id, double gain, bool muted)
    {
        public Guid Id { get; } = id;
        public double Gain { get; set; } = gain;
        public bool Muted { get; set; } = muted;
        public Endpoint? Endpoint { get; set; }
    }

    /// <summary>A participant's elements once they have offered.</summary>
    private sealed class Endpoint(WebRtcBin rtc, Element mixer, Element silence, Bin egress)
    {
        public object Sync { get; } = new();
        public WebRtcBin Rtc { get; } = rtc;
        public Element Mixer { get; } = mixer;
        public Element Silence { get; } = silence;
        public Bin Egress { get; } = egress;
        public Bin? Ingress { get; set; }
        public volatile Element? Tee;
        public bool Removed { get; set; }
    }

    private sealed class MixChain(Guid source, Guid listener, Bin bin, Element tee, Element mixer)
    {
        public Guid Source { get; } = source;
        public Guid Listener { get; } = listener;
        public Bin Bin { get; } = bin;
        public Element Tee { get; } = tee;
        public Element Mixer { get; } = mixer;
        public Pad? TeePad { get; set; }
        public Pad? MixerPad { get; set; }

        public void SetGain(float gain) => Bin.GetByName("amp")!.SetProperty("amplification", gain);

        public void SetPan(float pan) => Bin.GetByName("pan")!.SetProperty("panorama", pan);
    }
}
