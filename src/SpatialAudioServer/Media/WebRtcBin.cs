using System.Text.RegularExpressions;
using Gst;
using Gst.Sdp;
using Gst.WebRTC;
using DateTime = System.DateTime;
using Task = System.Threading.Tasks.Task;

namespace SpatialAudioServer.Media;

public sealed record IceCandidate(uint SdpMLineIndex, string Candidate);

/// <summary>A slice of an endpoint's local candidates, from index <c>Next - Candidates.Count</c> to <c>Next</c>.</summary>
public sealed record CandidateBatch(IReadOnlyList<IceCandidate> Candidates, int Next, bool Complete);

/// <summary>
/// Wraps one <c>webrtcbin</c> element. webrtcbin has no .gir, so every signal is reached by name; keeping them all
/// here means a misspelt signal can only live in one file. Used by the server's participant endpoints and by the
/// fake client.
/// </summary>
/// <remarks>
/// Callbacks (candidates, state, pad-added) arrive on webrtcbin's own thread or a streaming thread. The
/// offer/answer methods block on GstPromise replies from webrtcbin's thread, so never call them from a GStreamer
/// thread.
/// </remarks>
public sealed partial class WebRtcBin : IDisposable
{
    private readonly List<ulong> _handlers = [];
    private readonly List<IceCandidate> _candidates = [];
    private readonly List<string> _remoteCandidates = []; // for Diagnostics
    private readonly object _dataSync = new();
    private WebRTCDataChannel? _dataChannel; // ours or the remote's (an SL viewer's "SLData"); kept while the connection is
    private bool _dataOpen;
    private TaskCompletionSource _candidatesChanged = NewSignal();
    private bool _gatheringComplete;
    private bool _disposed;

    public WebRtcBin(string name, uint jitterBufferMs)
    {
        Element = ElementFactory.Make("webrtcbin", name) ?? throw new InvalidOperationException("webrtcbin is missing.");
        Global.UtilSetObjectArg(Element, "bundle-policy", "max-bundle");
        Element.SetProperty("latency", jitterBufferMs);
        DisableUpnp();

        _handlers.Add(Element.ConnectSignal("on-ice-candidate", (_, args) =>
        {
            if (args is [uint index, string candidate])
            {
                lock (_candidates)
                {
                    _candidates.Add(new IceCandidate(index, candidate));
                    if (_publicAddress is { } address && PublicCopy(candidate, address) is { } copy)
                    {
                        _candidates.Add(new IceCandidate(index, copy));
                    }
                    SignalCandidates();
                }
            }
            return null;
        }));

        _handlers.Add(Element.ConnectSignal("notify::ice-gathering-state", (_, _) =>
        {
            if (Element.GetProperty<WebRTCICEGatheringState>("ice-gathering-state") == WebRTCICEGatheringState.Complete)
            {
                lock (_candidates)
                {
                    _gatheringComplete = true;
                    SignalCandidates();
                }
            }
            return null;
        }));

        _handlers.Add(Element.ConnectSignal("on-negotiation-needed", (_, _) =>
        {
            NegotiationNeeded?.Invoke();
            return null;
        }));

        // The remote side's data channel (SL viewers always open "SLData"; browsers open none).
        _handlers.Add(Element.ConnectSignal("on-data-channel", (_, args) =>
        {
            if (args is [WebRTCDataChannel channel])
            {
                UseDataChannel(channel, open: true);
            }
            return null;
        }));

        _handlers.Add(Element.ConnectSignal("notify::connection-state", (_, _) =>
        {
            ConnectionStateChanged?.Invoke(Element.GetProperty<WebRTCPeerConnectionState>("connection-state"));
            return null;
        }));

        Element.PadAdded += OnPadAdded;
    }

    public Element Element { get; }

    /// <summary>The host's public address (1:1 NAT): local host candidates are also given with it.</summary>
    private System.Net.IPAddress? _publicAddress;

    /// <summary>
    /// Applies <c>Media:Network</c>: the ICE agent's port range (GstWebRTCICE min/max-rtp-port) and the public address
    /// that local host candidates are copied with. Call before negotiating.
    /// </summary>
    public void ConfigureNetwork(Configuration.MediaNetworkOptions network)
    {
        if (network.MinPort != 0 || network.MaxPort != 0)
        {
            var ice = NativeGObject.GetObjectProperty(Element.Handle, "ice-agent");
            try
            {
                // max first: the agent warns (and keeps the old value) when min would exceed the current max.
                NativeGObject.SetUIntProperty(ice, "max-rtp-port", network.MaxPort);
                NativeGObject.SetUIntProperty(ice, "min-rtp-port", network.MinPort);
            }
            finally
            {
                NativeGObject.Unref(ice);
            }
        }
        _publicAddress = System.Net.IPAddress.TryParse(network.PublicAddress, out var address) ? address : null;
    }

    /// <summary>
    /// A local host candidate ("candidate:1 1 UDP 2015363327 10.0.0.5 40012 typ host") as clients reach it through
    /// 1:1 NAT: the same with <paramref name="publicAddress"/>, its own foundation and a slightly lower priority (so a
    /// client on the same network still prefers the direct address). Null for anything else (another address family,
    /// server-reflexive or relayed candidates, the public address itself).
    /// </summary>
    public static string? PublicCopy(string candidate, System.Net.IPAddress publicAddress)
    {
        var parts = candidate.Split(' ');
        var typ = Array.IndexOf(parts, "typ");
        if (parts.Length < 8 || typ != 6 || parts[7] != "host" || !parts[0].StartsWith("candidate:", StringComparison.Ordinal)
            || !System.Net.IPAddress.TryParse(parts[4], out var local) || local.AddressFamily != publicAddress.AddressFamily
            || local.Equals(publicAddress) || !uint.TryParse(parts[3], out var priority))
        {
            return null;
        }
        parts[0] += "9"; // a foundation of its own (ICE foundations group candidates of the same base and type)
        parts[3] = Math.Max(1, priority - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        parts[4] = publicAddress.ToString();
        return string.Join(' ', parts);
    }

    /// <summary>Raised on webrtcbin's thread.</summary>
    public event Action<WebRTCPeerConnectionState>? ConnectionStateChanged;

    /// <summary>Raised on webrtcbin's thread when an offerer should create its offer. The server (answerer) ignores it.</summary>
    public event Action? NegotiationNeeded;

    /// <summary>Raised on webrtcbin's SCTP thread for each text message on the data channel (an SL viewer's SLData).</summary>
    public event Action<string>? DataMessage;

    /// <summary>Raised on webrtcbin's thread once the data channel can carry messages (<see cref="SendData"/>).</summary>
    public event Action? DataChannelOpened;

    /// <summary>The data channel's label once there is one ("SLData" for SL viewers).</summary>
    public string? DataChannelLabel { get; private set; }

    /// <summary>Raised on a streaming thread for each new src pad; link it before returning or data is lost.</summary>
    public event Action<Pad>? IncomingPad;

    public WebRTCPeerConnectionState ConnectionState => Element.GetProperty<WebRTCPeerConnectionState>("connection-state");

    /// <summary>Applies a remote offer. webrtcbin creates one transceiver per offered m-line.</summary>
    public void ApplyRemoteOffer(string offerSdp) => SetDescription("set-remote-description", WebRTCSDPType.Offer, offerSdp);

    /// <summary>
    /// After <see cref="ApplyRemoteOffer"/>: makes the transceiver for m-line <paramref name="mlineIndex"/> send as
    /// well as receive, restricted to <paramref name="codec"/>, and returns the sink pad <c>sink_{mlineIndex}</c> that
    /// feeds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the deterministic answerer order. Linking a send pad before the offer and letting webrtcbin match it
    /// to the offered m-line by its caps is timing-dependent: caps that arrive before the offer, or during it, leave
    /// the pad bound to a transceiver the answer never uses, and webrtcbin blocks it forever.
    /// </para>
    /// <para>
    /// GStreamer 1.28 creates a transceiver per offered m-line when the offer is applied, and that one is used.
    /// Older webrtcbin (1.24) creates them only with the answer, so there is none yet: then requesting
    /// <c>sink_{mlineIndex}</c> first creates a transceiver locked to that m-line (by index, not by caps), which the
    /// answer uses.
    /// </para>
    /// </remarks>
    public Pad PrepareSendPad(int mlineIndex, Caps codec)
    {
        Pad? pad = null;
        var transceiver = Element.EmitSignal<WebRTCRTPTransceiver>("get-transceiver", mlineIndex);
        if (transceiver is null)
        {
            pad = RequestSinkPad(mlineIndex);
            transceiver = pad.GetProperty<WebRTCRTPTransceiver>("transceiver")
                ?? throw new InvalidOperationException($"webrtcbin's sink_{mlineIndex} pad has no transceiver.");
        }
        using (transceiver)
        {
            Global.UtilSetObjectArg(transceiver, "direction", "sendrecv");
            transceiver.SetProperty("codec-preferences", codec);
        }
        return pad ?? RequestSinkPad(mlineIndex);
    }

    private Pad RequestSinkPad(int mlineIndex) =>
        Element.RequestPadSimple($"sink_{mlineIndex}") ?? throw new InvalidOperationException($"webrtcbin gave no sink_{mlineIndex} pad.");

    /// <summary>Creates and applies the local answer; returns its SDP text.</summary>
    public string CreateAndApplyAnswer()
    {
        using var answer = CreateDescription("create-answer", "answer");
        using var sdp = answer.GetSdp();
        Apply("set-local-description", answer);
        return sdp.AsText();
    }

    /// <summary>
    /// SDP lines, ending at CR, LF or CRLF. Firestorm's offer munging leaves a bare CR inside a line (it appends a second
    /// <c>a=fmtp</c> after the original line's CR), and GStreamer's SDP parser also treats CR as a line end.
    /// </summary>
    public static IEnumerable<string> SdpLines(string sdp) => sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Adds the gathered candidates to an answer's media sections (and <c>a=end-of-candidates</c> if gathering is
    /// complete), for clients that do not trickle: Second Life viewers only ever receive the answer SDP.
    /// </summary>
    public static string EmbedCandidates(string sdp, IReadOnlyList<IceCandidate> candidates, bool complete)
    {
        var output = new System.Text.StringBuilder();
        var section = -1;
        void Flush()
        {
            if (section < 0)
            {
                return;
            }
            foreach (var c in candidates.Where(c => c.SdpMLineIndex == section && !string.IsNullOrWhiteSpace(c.Candidate)))
            {
                output.Append(c.Candidate.StartsWith("a=", StringComparison.Ordinal) ? c.Candidate : "a=" + c.Candidate).Append("\r\n");
            }
            if (complete)
            {
                output.Append("a=end-of-candidates\r\n");
            }
        }
        foreach (var line in SdpLines(sdp))
        {
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                Flush();
                section++;
            }
            if (line.StartsWith("a=candidate:", StringComparison.Ordinal) || line == "a=end-of-candidates")
            {
                continue; // replaced by the full set below
            }
            output.Append(line).Append("\r\n");
        }
        Flush();
        return output.ToString();
    }

    /// <summary>Index of the first <c>m=audio</c> line in an SDP, or null.</summary>
    public static int? FirstAudioMLine(string sdp)
    {
        var index = 0;
        foreach (var line in SdpLines(sdp))
        {
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (line.StartsWith("m=audio", StringComparison.Ordinal))
                {
                    return index;
                }
                index++;
            }
        }
        return null;
    }

    /// <summary>Creates and applies a local offer and returns its SDP text.</summary>
    public string CreateOffer()
    {
        using var offer = CreateDescription("create-offer", "offer");
        using var sdp = offer.GetSdp();
        Apply("set-local-description", offer);
        return sdp.AsText();
    }

    /// <summary>
    /// Points webrtcbin at the configured STUN/TURN servers: the first <c>stun:</c> URL, and every <c>turn:</c> /
    /// <c>turns:</c> URL with the entry's credentials.
    /// </summary>
    /// <param name="id">Who the connection is for, in TURN credentials made from a secret.</param>
    public void ConfigureIceServers(IEnumerable<Configuration.IceServerConfig> servers, string id = "server")
    {
        var now = DateTimeOffset.UtcNow;
        var stunSet = false;
        foreach (var server in servers)
        {
            foreach (var url in server.Urls)
            {
                if (!stunSet && url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
                {
                    Element.SetProperty("stun-server", "stun://" + HostPort(url["stun:".Length..]));
                    stunSet = true;
                }
                else if (url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
                {
                    var scheme = url[..url.IndexOf(':')].ToLowerInvariant();
                    var (username, password) = server.CredentialsFor(id, now);
                    var credentials = username is null ? "" :
                        $"{System.Uri.EscapeDataString(username)}:{System.Uri.EscapeDataString(password ?? "")}@";
                    Element.EmitSignal("add-turn-server", $"{scheme}://{credentials}{HostPort(url[(scheme.Length + 1)..])}");
                }
            }
        }

        // "host:port?transport=udp" → "host:port" (webrtcbin's TURN URIs take no query).
        static string HostPort(string rest) => rest.Split('?')[0].TrimStart('/');
    }

    /// <summary>Creates an ordered data channel (before the offer, so it is negotiated with it).</summary>
    public void CreateDataChannel(string label)
    {
        using var options = Structure.NewFromString("options, ordered=(boolean)true")
            ?? throw new InvalidOperationException("Bad data channel options.");
        UseDataChannel(Element.EmitSignal<WebRTCDataChannel>("create-data-channel", label, options)
            ?? throw new InvalidOperationException("webrtcbin did not create the data channel."), open: false);
    }

    /// <summary>
    /// Sends a text message on the data channel; false (nothing sent) if there is none or it is not open yet.
    /// Safe from any thread except webrtcbin's own callbacks for this connection.
    /// </summary>
    public bool SendData(string text)
    {
        WebRTCDataChannel channel;
        lock (_dataSync)
        {
            if (_disposed || !_dataOpen || _dataChannel is null)
            {
                return false;
            }
            channel = _dataChannel;
        }
        return channel.SendStringFull(text);
    }

    private void UseDataChannel(WebRTCDataChannel channel, bool open)
    {
        lock (_dataSync)
        {
            if (_disposed)
            {
                return;
            }
            _dataChannel = channel;
            DataChannelLabel = channel.Label;
            channel.OnMessageString += OnDataMessage;
            channel.OnOpen += OnDataOpen;
            channel.OnClose += OnDataClose;
            // A remote channel announced by on-data-channel is already open (no on-open follows).
            _dataOpen = open || channel.ReadyState == WebRTCDataChannelState.Open;
        }
        if (_dataOpen)
        {
            DataChannelOpened?.Invoke();
        }
    }

    private void OnDataMessage(object? sender, WebRTCDataChannel.OnMessageStringSignalArgs e)
    {
        if (e.Data is { } text && !_disposed)
        {
            DataMessage?.Invoke(text);
        }
    }

    private void OnDataOpen(object? sender, EventArgs e)
    {
        lock (_dataSync)
        {
            _dataOpen = true;
        }
        DataChannelOpened?.Invoke();
    }

    private void OnDataClose(object? sender, EventArgs e)
    {
        lock (_dataSync)
        {
            _dataOpen = false;
        }
    }

    public void ApplyRemoteAnswer(string answerSdp) => SetDescription("set-remote-description", WebRTCSDPType.Answer, answerSdp);

    /// <summary>More than any real client gathers (a few per interface and protocol); the rest are refused.</summary>
    public const int MaxRemoteCandidates = 200;

    public void AddRemoteCandidate(IceCandidate candidate)
    {
        // An empty candidate is the browser's end-of-candidates marker; webrtcbin needs nothing for it.
        if (!string.IsNullOrWhiteSpace(candidate.Candidate))
        {
            lock (_remoteCandidates)
            {
                if (_remoteCandidates.Count >= MaxRemoteCandidates)
                {
                    throw new Rooms.LimitExceededException(
                        $"At most {MaxRemoteCandidates} ICE candidates per connection (send a new offer to start again).");
                }
                _remoteCandidates.Add(candidate.Candidate);
            }
            Element.EmitSignal("add-ice-candidate", candidate.SdpMLineIndex, candidate.Candidate);
        }
    }

    /// <summary>
    /// Asks webrtcbin for the connection's statistics (<c>get-stats</c>). Returns at once; the reply is read with
    /// <see cref="StatsRequest.ResultAsync"/>, which touches only the reply, so the element may go away meanwhile.
    /// Call it where the endpoint cannot be removed concurrently (the room worker).
    /// </summary>
    public StatsRequest RequestStats()
    {
        var (promise, replied) = NewPromise();
        Element.EmitSignal("get-stats", null, promise.Handle);
        return new StatsRequest(promise, replied);
    }

    /// <summary>A pending <c>get-stats</c>. Dispose it if <see cref="ResultAsync"/> is never awaited.</summary>
    public sealed class StatsRequest(Promise promise, Task replied) : IDisposable
    {
        /// <summary>The statistics, or null if webrtcbin did not answer in time.</summary>
        public async Task<ConnectionStats?> ResultAsync(TimeSpan timeout)
        {
            try
            {
                if (await Task.WhenAny(replied, Task.Delay(timeout)) != replied)
                {
                    promise.Interrupt();
                    return null;
                }
                using var reply = promise.GetReply();
                return reply is null ? null : Parse(reply);
            }
            finally
            {
                promise.Dispose();
            }
        }

        public void Dispose() => promise.Dispose();
    }

    /// <summary>
    /// What arrives from the client (inbound RTP), what goes to it (outbound), what the client reports about our
    /// stream (remote inbound, from RTCP), and the ICE candidate types of the pair in use.
    /// </summary>
    private static ConnectionStats Parse(Structure reply)
    {
        var stats = new ConnectionStats();
        var candidates = new Dictionary<string, string>(); // id → candidate type (host, srflx, relay…)
        string? localId = null, remoteId = null;
        for (uint i = 0; i < reply.NFields(); i++)
        {
            var id = reply.NthFieldName(i);
            using var s = reply.GetBoxed<Structure>(id);
            if (s is null)
            {
                continue;
            }
            if (id.StartsWith("rtp-inbound-stream-stats_", StringComparison.Ordinal))
            {
                stats = stats with
                {
                    PacketsReceived = U64(s, "packets-received"),
                    BytesReceived = U64(s, "bytes-received"),
                    PacketsLost = s.GetInt64("packets-lost", out var lost) ? lost : null,
                    JitterSeconds = s.GetDouble("jitter", out var jitter) ? jitter : null,
                };
            }
            else if (id.StartsWith("rtp-outbound-stream-stats_", StringComparison.Ordinal))
            {
                stats = stats with { PacketsSent = U64(s, "packets-sent"), BytesSent = U64(s, "bytes-sent") };
            }
            else if (id.StartsWith("rtp-remote-inbound-stream-stats_", StringComparison.Ordinal))
            {
                stats = stats with
                {
                    RoundTripSeconds = s.GetDouble("round-trip-time", out var rtt) ? rtt : null,
                    RemoteFractionLost = s.GetDouble("fraction-lost", out var fraction) ? fraction : null,
                };
            }
            else if (id.StartsWith("ice-candidate-pair_", StringComparison.Ordinal))
            {
                localId = s.GetString("local-candidate-id");
                remoteId = s.GetString("remote-candidate-id");
            }
            else if (id.StartsWith("ice-candidate-", StringComparison.Ordinal) && s.GetString("candidate-type") is { } type)
            {
                candidates[id] = type;
            }
        }
        if (localId is not null && remoteId is not null)
        {
            stats = stats with
            {
                IcePath = $"{candidates.GetValueOrDefault(localId, "?")} → {candidates.GetValueOrDefault(remoteId, "?")}",
            };
        }
        return stats;

        static ulong? U64(Structure s, string field) => s.GetUint64(field, out var value) ? value : null;
    }

    /// <summary>
    /// One line for logs when a connection fails: ICE state and every local and remote candidate as
    /// "type proto address:port", which shows whether the failure is ICE (no usable pair) or DTLS (ICE connected).
    /// </summary>
    public string Diagnostics()
    {
        string local, remote;
        lock (_candidates)
        {
            local = string.Join(", ", _candidates.Where(c => !string.IsNullOrWhiteSpace(c.Candidate)).Select(c => Summarize(c.Candidate)));
        }
        lock (_remoteCandidates)
        {
            remote = string.Join(", ", _remoteCandidates.Select(Summarize));
        }
        var ice = Element.GetProperty<WebRTCICEConnectionState>("ice-connection-state");
        var gathering = Element.GetProperty<WebRTCICEGatheringState>("ice-gathering-state");
        return $"ice={ice} gathering={gathering} local=[{local}] remote=[{remote}]";
    }

    /// <summary>"candidate:f 1 UDP 2015363327 10.2.2.235 43210 typ host" → "host UDP 10.2.2.235:43210".</summary>
    private static string Summarize(string candidate)
    {
        var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typ = Array.IndexOf(parts, "typ");
        return parts.Length >= 6 && typ > 0 && typ + 1 < parts.Length
            ? $"{parts[typ + 1]} {parts[2]} {parts[4]}:{parts[5]}"
            : candidate;
    }

    public CandidateBatch LocalCandidates(int since)
    {
        lock (_candidates)
        {
            since = Math.Clamp(since, 0, _candidates.Count);
            return new CandidateBatch(_candidates.Skip(since).ToList(), _candidates.Count, _gatheringComplete);
        }
    }

    /// <summary>
    /// Long-poll: waits until there are candidates beyond <paramref name="since"/>, gathering completes, or
    /// <paramref name="wait"/> passes, then returns what is there.
    /// </summary>
    public async Task<CandidateBatch> WaitForLocalCandidatesAsync(int since, TimeSpan wait, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            Task changed;
            lock (_candidates)
            {
                if (_candidates.Count > since || _gatheringComplete || _disposed)
                {
                    return LocalCandidates(since);
                }
                changed = _candidatesChanged.Task;
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return LocalCandidates(since);
            }
            try
            {
                await changed.WaitAsync(remaining, cancel);
            }
            catch (TimeoutException)
            {
                return LocalCandidates(since);
            }
        }
    }

    /// <summary>The first payload type the SDP maps to opus/48000, or null.</summary>
    public static int? OpusPayloadType(string sdp)
    {
        var match = OpusRtpMap().Match(sdp);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Disconnects the handlers. The element itself belongs to (and is released by) its pipeline.</summary>
    public void Dispose()
    {
        lock (_candidates)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            SignalCandidates();
        }
        foreach (var id in _handlers)
        {
            Element.RemoveHandler(id);
        }
        Element.PadAdded -= OnPadAdded;
        lock (_dataSync)
        {
            _dataOpen = false;
            if (_dataChannel is { } channel)
            {
                channel.OnMessageString -= OnDataMessage;
                channel.OnOpen -= OnDataOpen;
                channel.OnClose -= OnDataClose;
            }
        }
    }

    /// <summary>
    /// libnice enables UPnP IGD port mapping by default and starts a "gupnp-igd-thread" per ICE agent that outlives
    /// the agent: measured +2 threads per join in the churn test. A server has no use for UPnP, so it is turned off
    /// on the NiceAgent behind webrtcbin's ICE object (GstWebRTCNice "agent" → NiceAgent "upnp").
    /// </summary>
    /// <remarks>Raw GObject calls: see <see cref="NativeGObject"/> for why the binding cannot be used here.</remarks>
    private void DisableUpnp()
    {
        var ice = NativeGObject.GetObjectProperty(Element.Handle, "ice-agent");
        try
        {
            var agent = NativeGObject.GetObjectProperty(ice, "agent"); // absent unless the libnice ICE backend
            try
            {
                NativeGObject.SetBooleanProperty(agent, "upnp", false);
            }
            finally
            {
                NativeGObject.Unref(agent);
            }
        }
        finally
        {
            NativeGObject.Unref(ice);
        }
    }

    private void OnPadAdded(object? sender, Element.PadAddedSignalArgs args)
    {
        if (args.NewPad.Direction == PadDirection.Src)
        {
            IncomingPad?.Invoke(args.NewPad);
        }
    }

    private void SignalCandidates()
    {
        _candidatesChanged.TrySetResult();
        _candidatesChanged = NewSignal();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SetDescription(string signal, WebRTCSDPType type, string text)
    {
        if (SDPMessage.NewFromText(text, out var message) != SDPResult.Ok || message is null)
        {
            throw new ArgumentException("The SDP did not parse.");
        }
        using var description = WebRTCSessionDescription.New(type, message);
        Apply(signal, description);
    }

    private void Apply(string signal, WebRTCSessionDescription description)
    {
        var (promise, replied) = NewPromise();
        using (promise)
        {
            Element.EmitSignal(signal, description, promise.Handle);
            using var reply = Wait(promise, replied, signal);
        }
    }

    private WebRTCSessionDescription CreateDescription(string signal, string field)
    {
        var (promise, replied) = NewPromise();
        using (promise)
        {
            Element.EmitSignal(signal, null, promise.Handle);
            using var reply = Wait(promise, replied, signal) ?? throw new InvalidOperationException($"{signal} replied with nothing.");
            return reply.GetBoxed<WebRTCSessionDescription>(field)
                ?? throw new InvalidOperationException($"{signal} reply has no \"{field}\": {reply}");
        }
    }

    /// <summary>How long a webrtcbin promise may take. It normally replies in milliseconds.</summary>
    private static readonly TimeSpan PromiseTimeout = TimeSpan.FromSeconds(10);

    private static (Promise Promise, Task Replied) NewPromise()
    {
        var replied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (Promise.NewWithChangeFunc(_ => replied.TrySetResult()), replied.Task);
    }

    /// <summary>
    /// Waits for the reply with a timeout: an unanswered promise would otherwise block the caller (the room
    /// worker) forever.
    /// </summary>
    private static Structure? Wait(Promise promise, Task replied, string signal)
    {
        if (!replied.Wait(PromiseTimeout))
        {
            promise.Interrupt();
            throw new TimeoutException($"webrtcbin did not answer {signal} within {PromiseTimeout.TotalSeconds:F0} s.");
        }
        var result = promise.Wait();
        if (result != PromiseResult.Replied)
        {
            throw new InvalidOperationException($"{signal} promise ended as {result}.");
        }
        var reply = promise.GetReply();
        if (reply is not null && reply.HasField("error"))
        {
            var text = reply.ToString();
            reply.Dispose();
            // webrtcbin rejects descriptions it cannot use (bad SDP, no compatible media) this way.
            throw new ArgumentException($"{signal} failed: {text}");
        }
        return reply;
    }

    [GeneratedRegex(@"^a=rtpmap:(\d+) opus/48000", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex OpusRtpMap();
}

/// <summary>A connection's statistics (any may be missing early on). Rates come from comparing two readings.</summary>
/// <param name="RemoteFractionLost">Share of our packets the client reported lost in its last RTCP report (0–1).</param>
/// <param name="IcePath">Local → remote candidate types of the pair in use, e.g. "host → srflx".</param>
public sealed record ConnectionStats(
    ulong? PacketsReceived = null, ulong? BytesReceived = null, long? PacketsLost = null, double? JitterSeconds = null,
    ulong? PacketsSent = null, ulong? BytesSent = null, double? RoundTripSeconds = null, double? RemoteFractionLost = null,
    string? IcePath = null);
