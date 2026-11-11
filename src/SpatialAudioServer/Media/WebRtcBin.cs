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
    private TaskCompletionSource _candidatesChanged = NewSignal();
    private bool _gatheringComplete;
    private bool _disposed;

    public WebRtcBin(string name, uint jitterBufferMs)
    {
        Element = ElementFactory.Make("webrtcbin", name) ?? throw new InvalidOperationException("webrtcbin is missing.");
        Global.UtilSetObjectArg(Element, "bundle-policy", "max-bundle");
        Element.SetProperty("latency", jitterBufferMs);

        _handlers.Add(Element.ConnectSignal("on-ice-candidate", (_, args) =>
        {
            if (args is [uint index, string candidate])
            {
                lock (_candidates)
                {
                    _candidates.Add(new IceCandidate(index, candidate));
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

        _handlers.Add(Element.ConnectSignal("notify::connection-state", (_, _) =>
        {
            ConnectionStateChanged?.Invoke(Element.GetProperty<WebRTCPeerConnectionState>("connection-state"));
            return null;
        }));

        Element.PadAdded += OnPadAdded;
    }

    public Element Element { get; }

    /// <summary>Raised on webrtcbin's thread.</summary>
    public event Action<WebRTCPeerConnectionState>? ConnectionStateChanged;

    /// <summary>Raised on webrtcbin's thread when an offerer should create its offer. The server (answerer) ignores it.</summary>
    public event Action? NegotiationNeeded;

    /// <summary>Raised on a streaming thread for each new src pad; link it before returning or data is lost.</summary>
    public event Action<Pad>? IncomingPad;

    public WebRTCPeerConnectionState ConnectionState => Element.GetProperty<WebRTCPeerConnectionState>("connection-state");

    /// <summary>Applies a remote offer and returns the local answer's SDP text.</summary>
    public string AnswerOffer(string offerSdp)
    {
        SetDescription("set-remote-description", WebRTCSDPType.Offer, offerSdp);
        using var answer = CreateDescription("create-answer", "answer");
        using var sdp = answer.GetSdp();
        Apply("set-local-description", answer);
        return sdp.AsText();
    }

    /// <summary>Creates and applies a local offer and returns its SDP text.</summary>
    public string CreateOffer()
    {
        using var offer = CreateDescription("create-offer", "offer");
        using var sdp = offer.GetSdp();
        Apply("set-local-description", offer);
        return sdp.AsText();
    }

    public void ApplyRemoteAnswer(string answerSdp) => SetDescription("set-remote-description", WebRTCSDPType.Answer, answerSdp);

    public void AddRemoteCandidate(IceCandidate candidate)
    {
        // An empty candidate is the browser's end-of-candidates marker; webrtcbin needs nothing for it.
        if (!string.IsNullOrWhiteSpace(candidate.Candidate))
        {
            Element.EmitSignal("add-ice-candidate", candidate.SdpMLineIndex, candidate.Candidate);
        }
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
        using var promise = Promise.New();
        Element.EmitSignal(signal, description, promise.Handle);
        using var reply = Wait(promise, signal);
    }

    private WebRTCSessionDescription CreateDescription(string signal, string field)
    {
        using var promise = Promise.New();
        Element.EmitSignal(signal, null, promise.Handle);
        using var reply = Wait(promise, signal) ?? throw new InvalidOperationException($"{signal} replied with nothing.");
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
            // webrtcbin rejects descriptions it cannot use (bad SDP, no compatible media) this way.
            throw new ArgumentException($"{signal} failed: {text}");
        }
        return reply;
    }

    [GeneratedRegex(@"^a=rtpmap:(\d+) opus/48000", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex OpusRtpMap();
}
