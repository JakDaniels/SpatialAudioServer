using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gst;
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

    private int _receiving;
    private int _remoteCandidates;
    private volatile Exception? _iceError;
    private int _disposed;
    private string? _sessionToken;

    public FakeClient(
        string name, double toneHz, IReadOnlyList<double> listenFor, uint jitterBufferMs = 100, ILogger? log = null, double volume = 0.1)
    {
        Name = name;
        ToneHz = toneHz;
        Meter = new ToneMeter(listenFor) { Threshold = volume / 5 };
        _pipeline = Pipeline.New($"fake-{name}");
        _rtc = new WebRtcBin("webrtc", jitterBufferMs);
        _rtc.NegotiationNeeded += _negotiationNeeded.Set;
        _rtc.IncomingPad += OnIncomingPad;

        var send = Global.ParseBinFromDescription(
            $"audiotestsrc is-live=true wave=sine freq={toneHz.ToString(CultureInfo.InvariantCulture)} volume={volume.ToString(CultureInfo.InvariantCulture)} ! " +
            "audioconvert ! audioresample ! audio/x-raw,rate=48000,channels=1 ! opusenc ! rtpopuspay pt=111 ! " +
            "capsfilter caps=\"application/x-rtp,media=audio,encoding-name=OPUS,payload=111,clock-rate=48000\"",
            ghostUnlinkedPads: true);
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

    public Guid ParticipantId { get; private set; }

    public bool Receiving => Volatile.Read(ref _receiving) == 1;

    /// <summary>Server candidates received and applied so far.</summary>
    public int RemoteCandidates => Volatile.Read(ref _remoteCandidates);

    /// <summary>Why the candidate exchange stopped early, if it did.</summary>
    public Exception? IceError => _iceError;

    /// <summary>Offers to the server with the participant's join token and keeps trickling ICE until disposed.</summary>
    public async Task ConnectAsync(HttpClient http, Guid participantId, string joinToken, CancellationToken cancel = default)
    {
        ParticipantId = participantId;
        if (_pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
        {
            throw new InvalidOperationException("Fake client pipeline refused to play.");
        }
        if (!_negotiationNeeded.Wait(TimeSpan.FromSeconds(5), cancel))
        {
            throw new TimeoutException("webrtcbin never asked for negotiation.");
        }

        var offer = await Task.Run(_rtc.CreateOffer, cancel);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"signal/{participantId}/offer")
        {
            Content = JsonContent.Create(new OfferRequest(offer)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", joinToken);
        using var response = await http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Offer failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancel)}");
        }
        var answer = (await response.Content.ReadFromJsonAsync<OfferResponse>(Json, cancel))!;
        _sessionToken = answer.SessionToken;
        await Task.Run(() => _rtc.ApplyRemoteAnswer(answer.Sdp), cancel);

        _iceLoop = Task.Run(() => TrickleAsync(http, _stop.Token), CancellationToken.None);
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
        _pipeline.SetState(State.Null);
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

                using var get = new HttpRequestMessage(HttpMethod.Get, $"signal/{ParticipantId}/ice?since={received}&waitMs=500");
                get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sessionToken);
                using var response = await http.SendAsync(get, cancel);
                if (!response.IsSuccessStatusCode)
                {
                    return; // participant removed
                }
                var remote = (await response.Content.ReadFromJsonAsync<CandidatesResponse>(Json, cancel))!;
                foreach (var c in remote.Candidates)
                {
                    _rtc.AddRemoteCandidate(new IceCandidate(c.SdpMLineIndex, c.Candidate!));
                    Interlocked.Increment(ref _remoteCandidates);
                }
                received = remote.Next;
                if (remote.Complete && local.Complete && remote.State == Rooms.ParticipantState.Connected)
                {
                    await Task.Delay(500, cancel); // nothing left to exchange; idle until disposed
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
            }
            return PadProbeReturn.Ok;
        });
        receive.SyncStateWithParent();
        pad.Link(receive.GetStaticPad("sink")!);
    }
}
