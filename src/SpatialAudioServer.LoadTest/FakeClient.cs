using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gst;
using Gst.App;
using Microsoft.Extensions.Logging;
using SpatialAudioServer.Media;
using SpatialAudioServer.Signalling;
using Task = System.Threading.Tasks.Task;

namespace SpatialAudioServer.LoadTest;

/// <summary>
/// A GStreamer stand-in for a browser participant: sends a sine tone at its own frequency through the server's
/// signalling API (offer with the join token, trickle ICE both ways) and measures the tones in the mix it receives.
/// </summary>
public sealed class FakeClient : IDisposable
{
    private readonly Pipeline _pipeline;
    private readonly WebRtcBin _rtc;
    private readonly ManualResetEventSlim _negotiationNeeded = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly BusWatcher _bus;
    private readonly ILogger _log;
    private Task? _iceLoop;
    /// <summary>The server's JSON conventions: camelCase, enums as camelCase strings.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly double _volume;
    private readonly Element _tone;
    private int _receiving;
    private long _lastAudio = long.MinValue / 2; // TickCount64 of the last decoded buffer received
    private volatile bool _removed;
    private int _remoteCandidates;
    private volatile Exception? _iceError;
    private int _disposed;
    private string? _sessionToken;
    private readonly Thread? _clipPump;
    private readonly TaskCompletionSource _dataOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _dataMessages = [];

    public FakeClient(
        string name, double toneHz, IReadOnlyList<double> listenFor, uint jitterBufferMs = 100, ILogger? log = null, double volume = 0.1,
        bool talking = true, bool viewerStyle = false, AudioClip? clip = null, int clipOffsetBytes = 0, double? threshold = null)
    {
        _volume = volume;
        ViewerStyle = viewerStyle;
        Name = name;
        ToneHz = toneHz;
        Meter = new ToneMeter(listenFor) { Threshold = threshold ?? volume / 5 };
        _pipeline = Pipeline.New($"fake-{name}");
        _rtc = new WebRtcBin("webrtc", jitterBufferMs);
        _rtc.NegotiationNeeded += _negotiationNeeded.Set;
        _rtc.IncomingPad += OnIncomingPad;
        _rtc.DataChannelOpened += () => _dataOpen.TrySetResult();
        _rtc.DataMessage += message =>
        {
            lock (_dataMessages)
            {
                _dataMessages.Add(message);
            }
        };

        // The "tone" element has a volume property either way: the tone generator, or a volume element after the clip.
        var source = clip is null
            ? $"audiotestsrc name=tone is-live=true wave=sine freq={toneHz.ToString(CultureInfo.InvariantCulture)} " +
              $"volume={(talking ? volume : 0).ToString(CultureInfo.InvariantCulture)}"
            : $"appsrc name=clip format=time is-live=true block=true max-time=100000000 caps=\"{AudioClip.Caps}\" ! clocksync ! " +
              $"volume name=tone volume={(talking ? volume : 0).ToString(CultureInfo.InvariantCulture)}";
        var send = Global.ParseBinFromDescription(
            source + " ! " +
            "audioconvert ! audioresample ! audio/x-raw,rate=48000,channels=1 ! opusenc ! rtpopuspay pt=111 ! " +
            "capsfilter caps=\"application/x-rtp,media=audio,encoding-name=OPUS,payload=111,clock-rate=48000" +
            // A viewer's offer has an Opus a=fmtp line (which its munging then duplicates); caps fields become fmtp.
            (viewerStyle ? ",useinbandfec=(string)1" : "") + "\"",
            ghostUnlinkedPads: true);
        _tone = send.GetByName("tone")!;
        if (clip is not null)
        {
            var appsrc = send.GetByName("clip") as AppSrc ?? throw new InvalidOperationException("appsrc has no AppSrc wrapper.");
            _clipPump = new Thread(() => PumpClip(appsrc, clip, clipOffsetBytes)) { IsBackground = true, Name = $"clip-{name}" };
        }
        Talking = talking;
        _pipeline.Add(_rtc.Element);
        _pipeline.Add(send);
        var linked = send.GetStaticPad("src")!.Link(_rtc.Element.RequestPadSimple("sink_%u")!);
        if (linked != PadLinkReturn.Ok)
        {
            throw new InvalidOperationException($"Fake client send branch: {linked}");
        }
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _bus = new BusWatcher(_pipeline, $"fake-{name}", _log);
        _rtc.ConnectionStateChanged += state =>
        {
            if (state == Gst.WebRTC.WebRTCPeerConnectionState.Failed)
            {
                _log.LogWarning("{Name}: connection failed: {Diagnostics}", Name, _rtc.Diagnostics());
            }
        };
    }

    public string Name { get; }

    public double ToneHz { get; }

    public ToneMeter Meter { get; }

    /// <summary>The signalling session token from the offer response (tests use it to call signalling directly).</summary>
    public string? SessionToken => _sessionToken;

    public Guid ParticipantId { get; private set; }

    public bool Receiving => Volatile.Read(ref _receiving) == 1;

    /// <summary>Audio from the server arrived within the last 2 seconds.</summary>
    public bool AudioFlowing => Environment.TickCount64 - Volatile.Read(ref _lastAudio) < 2000;

    /// <summary>
    /// The server no longer knows this participant (kicked, or the room deleted): its signalling poll got 401/404.
    /// Viewer-style clients don't poll, so they never notice.
    /// </summary>
    public bool Removed => _removed;

    /// <summary>Whether the client is sending its tone (otherwise silence). Can change while connected.</summary>
    public bool Talking { get; private set; }

    public void SetTalking(bool talking)
    {
        _tone.SetProperty("volume", talking ? _volume : 0.0);
        Talking = talking;
    }

    /// <summary>
    /// Behaves like a Second Life viewer (Firestorm's llwebrtc): an ordered "SLData" data channel created before
    /// the offer, the viewer's SDP munging, a non-trickle answer, and server candidates never fetched.
    /// </summary>
    public bool ViewerStyle { get; }

    /// <summary>Completes when the data channel (viewer style: "SLData") is open.</summary>
    public Task DataChannelOpen => _dataOpen.Task;

    /// <summary>The text messages the server has sent on the data channel so far.</summary>
    public IReadOnlyList<string> DataMessages
    {
        get
        {
            lock (_dataMessages)
            {
                return [.. _dataMessages];
            }
        }
    }

    /// <summary>Sends a text message on the data channel; false if it is not open.</summary>
    public bool SendData(string text) => _rtc.SendData(text);

    /// <summary>
    /// What a viewer sends as it moves (llvoicewebrtc.cpp sendPositionUpdate): avatar and camera, global coordinates
    /// in centimetres, quaternions ×100, all as integers.
    /// </summary>
    public bool SendViewerPose(Rooms.Vec3 avatar, Rooms.Quat avatarRotation, Rooms.Vec3 camera, Rooms.Quat cameraRotation)
    {
        static string P(Rooms.Vec3 v) => $"{{\"x\":{(int)(v.X * 100)},\"y\":{(int)(v.Y * 100)},\"z\":{(int)(v.Z * 100)}}}";
        static string Q(Rooms.Quat q) => $"{{\"x\":{(int)(q.X * 100)},\"y\":{(int)(q.Y * 100)},\"z\":{(int)(q.Z * 100)},\"w\":{(int)(q.W * 100)}}}";
        return SendData($"{{\"sp\":{P(avatar)},\"sh\":{Q(avatarRotation)},\"lp\":{P(camera)},\"lh\":{Q(cameraRotation)}}}");
    }

    /// <summary>The server's answer SDP (once connected).</summary>
    public string? AnswerSdp { get; private set; }

    /// <summary>Server candidates received and applied so far.</summary>
    public int RemoteCandidates => Volatile.Read(ref _remoteCandidates);

    /// <summary>Why the candidate exchange stopped early, if it did.</summary>
    public Exception? IceError => _iceError;

    /// <summary>Offers to the server with the participant's join token and keeps trickling ICE until disposed.</summary>
    /// <param name="inWorldAvatar">Ask the room's integration to show this client in-world (an OpenSim NPC).</param>
    public async Task ConnectAsync(HttpClient http, Guid participantId, string joinToken, CancellationToken cancel = default,
        string? displayName = null, bool inWorldAvatar = false)
    {
        ParticipantId = participantId;
        if (_pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
        {
            throw new InvalidOperationException("Fake client pipeline refused to play.");
        }
        _clipPump?.Start();
        if (!_negotiationNeeded.Wait(TimeSpan.FromSeconds(5), cancel))
        {
            throw new TimeoutException("webrtcbin never asked for negotiation.");
        }

        if (ViewerStyle)
        {
            _rtc.CreateDataChannel("SLData");
        }
        var offer = await Task.Run(_rtc.CreateOffer, cancel);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"signal/{participantId}/offer")
        {
            Content = JsonContent.Create(ViewerStyle
                ? new OfferRequest(MungeLikeViewer(offer), Trickle: false, DisplayName: displayName, InWorldAvatar: inWorldAvatar ? true : null)
                : new OfferRequest(offer, DisplayName: displayName, InWorldAvatar: inWorldAvatar ? true : null)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", joinToken);
        using var response = await http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Offer failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancel)}");
        }
        var answer = (await response.Content.ReadFromJsonAsync<OfferResponse>(Json, cancel))!;
        _sessionToken = answer.SessionToken;
        AnswerSdp = answer.Sdp;
        await Task.Run(() => _rtc.ApplyRemoteAnswer(answer.Sdp), cancel);

        _iceLoop = Task.Run(() => TrickleAsync(http, _stop.Token), CancellationToken.None);
    }

    /// <summary>Sets its own listener orientation through signalling (session token), as the web client's Facing does.</summary>
    public async Task SetListenerOrientationAsync(HttpClient http, Rooms.Quat orientation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"signal/{ParticipantId}/pose")
        {
            Content = JsonContent.Create(new { listenerOrientation = orientation }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sessionToken);
        (await http.SendAsync(request)).EnsureSuccessStatusCode();
    }

    /// <summary>Leaves through the signalling API (the server removes the participant).</summary>
    public async Task LeaveAsync(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"signal/{ParticipantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sessionToken);
        (await http.SendAsync(request)).EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _stop.Cancel();
        try
        {
            _iceLoop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        _pipeline.SetState(State.Null); // unblocks the clip pump's push (it returns Flushing)
        if (_clipPump is { IsAlive: true })
        {
            _clipPump.Join(TimeSpan.FromSeconds(2));
        }
        _bus.Dispose();
        _rtc.Dispose();
        _pipeline.Dispose();
        _negotiationNeeded.Dispose();
        _stop.Dispose();
    }

    private async Task TrickleAsync(HttpClient http, CancellationToken cancel)
    {
        int sent = 0, received = 0;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var local = _rtc.LocalCandidates(sent);
                if (local.Candidates.Count > 0)
                {
                    using var post = new HttpRequestMessage(HttpMethod.Post, $"signal/{ParticipantId}/ice")
                    {
                        Content = JsonContent.Create(new CandidatesRequest(
                            local.Candidates.Select(c => new CandidateDto(c.Candidate, c.SdpMLineIndex)).ToList(), local.Complete)),
                    };
                    post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sessionToken);
                    (await http.SendAsync(post, cancel)).EnsureSuccessStatusCode();
                    sent = local.Next;
                }

                if (ViewerStyle)
                {
                    // A viewer only sends its candidates (VoiceSignalingRequest); the server's came in the answer.
                    if (local.Complete)
                    {
                        return;
                    }
                    await Task.Delay(100, cancel);
                    continue;
                }

                using var get = new HttpRequestMessage(HttpMethod.Get, $"signal/{ParticipantId}/ice?since={received}&waitMs=500");
                get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sessionToken);
                using var response = await http.SendAsync(get, cancel);
                if (!response.IsSuccessStatusCode)
                {
                    _removed = response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized
                        or System.Net.HttpStatusCode.Gone;
                    return; // participant removed
                }
                var remote = (await response.Content.ReadFromJsonAsync<CandidatesResponse>(Json, cancel))!;
                foreach (var c in remote.Candidates)
                {
                    _rtc.AddRemoteCandidate(new IceCandidate(c.SdpMLineIndex, c.Candidate!));
                    Interlocked.Increment(ref _remoteCandidates);
                }
                received = remote.Next;
                if (remote.Complete)
                {
                    // The server has no more candidates, so its long-poll now returns at once: pace the loop (it
                    // still sends our own late candidates and notices removal). Without this it spins until
                    // connected and runs into the signalling rate limit. Once connected it only watches for removal:
                    // every 2 s keeps hundreds of clients from one address under the per-address limit (300/s).
                    var idle = local.Complete && remote.State == Rooms.ParticipantState.Connected;
                    await Task.Delay(idle ? 2000 : 200, cancel);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Without this the loop would die silently and ICE would just fail later (or, on one machine, still
            // connect from one-sided candidates and hide the bug).
            _iceError = ex;
            _log.LogError(ex, "{Name}: ICE candidate exchange stopped", Name);
        }
    }

    /// <summary>
    /// Firestorm's offer munging (indra/llwebrtc/llwebrtc.cpp, OnSuccess), reproduced byte for byte: the Opus rtpmap
    /// is rewritten with a bare LF, and the original fmtp line (still ending in CR) is followed by a second fmtp line.
    /// </summary>
    public static string MungeLikeViewer(string sdp)
    {
        var output = new System.Text.StringBuilder();
        var opusPayload = "";
        foreach (var line in sdp.Split('\n'))
        {
            var rtpmap = System.Text.RegularExpressions.Regex.Match(line, @"^a=rtpmap:(\d+) opus/(\d+)/2");
            if (rtpmap.Success)
            {
                opusPayload = rtpmap.Groups[1].Value;
                output.Append("a=rtpmap:").Append(opusPayload).Append(" opus/48000/2\n");
            }
            else if (opusPayload.Length > 0 && line.StartsWith("a=fmtp:" + opusPayload, StringComparison.Ordinal))
            {
                output.Append(line).Append("a=fmtp:").Append(opusPayload)
                    .Append(" minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxplaybackrate=48000;sprop-maxplaybackrate=48000;sprop-maxcapturerate=48000\n");
            }
            else if (line.Length > 0)
            {
                output.Append(line).Append('\n');
            }
        }
        return output.ToString();
    }

    /// <summary>
    /// Feeds the clip to appsrc in 20 ms buffers, looping, until the pipeline stops. appsrc blocks when 100 ms are
    /// queued, so this thread runs at the rate the send path consumes. clocksync makes that real time explicitly
    /// (measured: the send path already paces without it, but that depends on webrtcbin internals).
    /// </summary>
    private void PumpClip(AppSrc appsrc, AudioClip clip, int offset)
    {
        const int chunkBytes = AudioClip.Rate / 50 * 2;
        var chunk = new byte[chunkBytes];
        var pcm = clip.Pcm;
        var position = (offset & ~1) % pcm.Length;
        long ms = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                for (var filled = 0; filled < chunkBytes;)
                {
                    var n = Math.Min(chunkBytes - filled, pcm.Length - position);
                    pcm.AsSpan(position, n).CopyTo(chunk.AsSpan(filled));
                    filled += n;
                    position = (position + n) % pcm.Length;
                }
                var buffer = Gst.Buffer.NewMemdup(chunk);
                buffer.SetPts(ClockTime.FromMilliseconds(ms));
                buffer.SetDuration(ClockTime.FromMilliseconds(20));
                ms += 20;
                if (appsrc.PushBuffer(buffer) != FlowReturn.Ok)
                {
                    return; // flushing: the pipeline is stopping
                }
            }
        }
        catch (Exception ex) when (Volatile.Read(ref _disposed) == 0)
        {
            _log.LogError(ex, "{Name}: audio clip pump stopped", Name);
        }
    }

    private void OnIncomingPad(Pad pad)
    {
        if (Interlocked.Exchange(ref _receiving, 1) != 0)
        {
            return;
        }
        var receive = Global.ParseBinFromDescription(
            "queue ! rtpopusdepay ! opusdec ! audioconvert ! audio/x-raw,format=S16LE,rate=48000,channels=2 ! fakesink name=out sync=false",
            ghostUnlinkedPads: true);
        _pipeline.Add(receive);
        receive.GetByName("out")!.GetStaticPad("sink")!.AddProbe(PadProbeType.Buffer, (_, info) =>
        {
            using var buffer = info.GetBuffer();
            if (buffer is not null)
            {
                using var map = buffer.Map(MapFlags.Read);
                Meter.Add(MemoryMarshal.Cast<byte, short>(map.Span), channels: 2);
                Volatile.Write(ref _lastAudio, Environment.TickCount64);
            }
            return PadProbeReturn.Ok;
        });
        receive.SyncStateWithParent();
        pad.Link(receive.GetStaticPad("sink")!);
    }
}
