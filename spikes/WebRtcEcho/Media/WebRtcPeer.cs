using System.Text.RegularExpressions;
using Gst;
using Gst.GLib;
using Gst.Sdp;
using Gst.WebRTC;

namespace WebRtcEcho.Media;

public sealed record IceCandidate(uint SdpMLineIndex, string Candidate);

/// <summary>
/// One pipeline holding one webrtcbin. Wraps the parts of webrtcbin that the binding can only reach by
/// signal name (it has no .gir), and owns a bus-polling thread for errors and warnings.
/// Shared by the server-side echo session and the fake browser client.
/// </summary>
/// <remarks>
/// Threading: webrtcbin runs its own thread for on-ice-candidate / notify / promise callbacks, and
/// pad-added arrives on a streaming thread. Handlers here only touch locked or volatile state.
/// The Offer/Answer methods block on promises, so they must never be called from a webrtcbin thread.
/// </remarks>
public abstract class WebRtcPeer : IDisposable
{
    private readonly List<ulong> _handlers = [];
    private readonly List<IceCandidate> _localCandidates = [];
    private readonly CancellationTokenSource _stop = new();
    private Thread? _busThread;
    private volatile bool _gatheringComplete;
    private volatile string _connectionState = "new";
    private bool _disposed;

    protected WebRtcPeer(string description, ILogger log)
    {
        Log = log;
        Pipeline = Global.ParseLaunch(description) as Pipeline
            ?? throw new InvalidOperationException("The description did not produce a pipeline.");
        WebRtc = Pipeline.GetByName("webrtc") ?? throw new InvalidOperationException("No element named webrtc.");

        _handlers.Add(WebRtc.ConnectSignal("on-ice-candidate", (_, args) =>
        {
            if (args is [uint index, string candidate])
            {
                lock (_localCandidates)
                {
                    _localCandidates.Add(new IceCandidate(index, candidate));
                }
                OnLocalCandidate(new IceCandidate(index, candidate));
            }
            return null;
        }));

        _handlers.Add(WebRtc.ConnectSignal("notify::ice-gathering-state", (_, _) =>
        {
            var state = WebRtc.GetProperty<WebRTCICEGatheringState>("ice-gathering-state");
            Log.LogDebug("ice-gathering-state {State}", state);
            if (state == WebRTCICEGatheringState.Complete)
            {
                _gatheringComplete = true;
            }
            return null;
        }));

        _handlers.Add(WebRtc.ConnectSignal("notify::connection-state", (_, _) =>
        {
            var state = WebRtc.GetProperty<WebRTCPeerConnectionState>("connection-state");
            _connectionState = state.ToString().ToLowerInvariant();
            Log.LogInformation("connection-state {State}", state);
            return null;
        }));

        WebRtc.PadAdded += OnPadAddedSafe;
    }

    protected ILogger Log { get; }
    protected Pipeline Pipeline { get; }
    protected Element WebRtc { get; }

    public bool GatheringComplete => _gatheringComplete;
    public string ConnectionState => _connectionState;

    public void Start()
    {
        if (Pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
        {
            throw new InvalidOperationException("The pipeline refused to go to PLAYING.");
        }
        _busThread = new Thread(PollBus) { IsBackground = true, Name = "gst-bus" };
        _busThread.Start();
    }

    /// <summary>Local candidates from index <paramref name="since"/> on.</summary>
    public IReadOnlyList<IceCandidate> LocalCandidates(int since)
    {
        lock (_localCandidates)
        {
            return _localCandidates.Skip(Math.Max(0, since)).ToList();
        }
    }

    public void AddRemoteCandidate(IceCandidate candidate)
    {
        // An empty candidate is the browser's end-of-candidates marker; webrtcbin needs nothing for it.
        if (string.IsNullOrWhiteSpace(candidate.Candidate))
        {
            return;
        }
        WebRtc.EmitSignal("add-ice-candidate", candidate.SdpMLineIndex, candidate.Candidate);
    }

    /// <summary>Applies a remote offer and returns the local answer's SDP text.</summary>
    protected string AnswerOffer(string offerSdp)
    {
        SetDescription("set-remote-description", WebRTCSDPType.Offer, offerSdp);
        using var answer = CreateDescription("create-answer", "answer");
        using (var sdp = answer.GetSdp())
        {
            Apply("set-local-description", answer);
            return sdp.AsText();
        }
    }

    /// <summary>Creates and applies a local offer and returns its SDP text.</summary>
    protected string CreateOffer()
    {
        using var offer = CreateDescription("create-offer", "offer");
        using var sdp = offer.GetSdp();
        Apply("set-local-description", offer);
        return sdp.AsText();
    }

    protected void ApplyRemoteAnswer(string answerSdp) =>
        SetDescription("set-remote-description", WebRTCSDPType.Answer, answerSdp);

    /// <summary>The first payload type the SDP maps to opus/48000, or null.</summary>
    protected static int? OpusPayloadType(string sdp)
    {
        var match = Regex.Match(sdp, @"^a=rtpmap:(\d+) opus/48000", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    protected virtual void OnLocalCandidate(IceCandidate candidate)
    {
    }

    protected abstract void OnPadAdded(Pad pad);

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
        using var promise = Promise.New();
        WebRtc.EmitSignal(signal, description, promise.Handle);
        using var reply = Wait(promise, signal);
    }

    private WebRTCSessionDescription CreateDescription(string signal, string field)
    {
        using var promise = Promise.New();
        WebRtc.EmitSignal(signal, null, promise.Handle);
        using var reply = Wait(promise, signal)
            ?? throw new InvalidOperationException($"{signal} replied with nothing.");
        return reply.GetBoxed<WebRTCSessionDescription>(field)
            ?? throw new InvalidOperationException($"{signal} reply has no \"{field}\": {reply}");
    }

    private static Structure? Wait(Promise promise, string signal)
    {
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
            throw new InvalidOperationException($"{signal} failed: {text}");
        }
        return reply;
    }

    private void OnPadAddedSafe(object? sender, Element.PadAddedSignalArgs args)
    {
        try
        {
            if (args.NewPad.Direction == PadDirection.Src)
            {
                OnPadAdded(args.NewPad);
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "pad-added handler failed");
        }
    }

    private void PollBus()
    {
        using var bus = Pipeline.GetBus();
        var interval = ClockTime.FromMilliseconds(200);
        while (!_stop.IsCancellationRequested)
        {
            using var message = bus.TimedPopFiltered(interval, MessageType.Error | MessageType.Warning | MessageType.Eos);
            if (message is null)
            {
                continue;
            }
            switch (message.Type)
            {
                case MessageType.Error:
                    {
                        (GException error, string? debug) = message.ParseError();
                        Log.LogError("GStreamer error from {Source}: {Message} ({Debug})", message.SourceName, error.Message, debug);
                        break;
                    }
                case MessageType.Warning:
                    {
                        (GException warning, string? debug) = message.ParseWarning();
                        Log.LogWarning("GStreamer warning from {Source}: {Message} ({Debug})", message.SourceName, warning.Message, debug);
                        break;
                    }
                default:
                    Log.LogInformation("End of stream");
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // NULL first: it joins webrtcbin's thread, so no handler runs after this returns.
        Pipeline.SetState(State.Null);
        _stop.Cancel();
        _busThread?.Join();
        foreach (var id in _handlers)
        {
            WebRtc.RemoveHandler(id);
        }
        WebRtc.PadAdded -= OnPadAddedSafe;
        DisposeCore();
        Pipeline.Dispose();
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    protected virtual void DisposeCore()
    {
    }
}
