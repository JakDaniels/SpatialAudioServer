// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using Gst;
using DateTime = System.DateTime;
using Task = System.Threading.Tasks.Task;

namespace WebRtcEcho.Media;

/// <summary>
/// A GStreamer stand-in for the browser: offers a 440 Hz Opus stream over the same HTTP API the web page
/// uses, and measures what comes back. Echoed tone shows up as loud buffers; the server's silence
/// keep-alive alone gives none.
/// </summary>
public sealed class FakeClient : WebRtcPeer
{
    private const string ReceiveBranch =
        "queue ! rtpopusdepay ! opusdec ! audioconvert ! audio/x-raw,format=S16LE,rate=48000,channels=2 ! " +
        "fakesink name=out sync=false";

    private const short LoudThreshold = 1000;

    private readonly ManualResetEventSlim _negotiationNeeded = new();
    private int _branchBuilt;
    private long _buffers;
    private long _loudBuffers;

    public FakeClient(ILogger log)
        : base(
            "webrtcbin name=webrtc bundle-policy=max-bundle " +
            "audiotestsrc is-live=true wave=sine freq=440 volume=0.3 ! audioconvert ! audioresample ! " +
            "audio/x-raw,rate=48000,channels=1 ! opusenc ! rtpopuspay pt=111 ! " +
            "application/x-rtp,media=audio,encoding-name=OPUS,payload=111,clock-rate=48000 ! webrtc.",
            log)
    {
        WebRtc.ConnectSignal("on-negotiation-needed", (_, _) =>
        {
            _negotiationNeeded.Set();
            return null;
        });
    }

    /// <summary>Runs one call against the server; returns 0 when the tone came back.</summary>
    public async Task<int> RunAsync(System.Uri server, TimeSpan duration, CancellationToken cancel)
    {
        using var http = new HttpClient { BaseAddress = server };

        Start();
        if (!_negotiationNeeded.Wait(TimeSpan.FromSeconds(5), cancel))
        {
            Log.LogError("webrtcbin never asked for negotiation");
            return 1;
        }

        var offer = await Task.Run(CreateOffer, cancel);
        var response = await http.PostAsJsonAsync("api/sessions", new OfferRequest(offer), cancel);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<OfferResponse>(cancel)
            ?? throw new InvalidOperationException("Empty answer.");
        Log.LogInformation("Session {Id} answered", answer.Id);
        await Task.Run(() => ApplyRemoteAnswer(answer.Sdp), cancel);

        int sent = 0, received = 0;
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var c in LocalCandidates(sent))
            {
                await http.PostAsJsonAsync(
                    $"api/sessions/{answer.Id}/candidates", new CandidateDto(c.Candidate, c.SdpMLineIndex), cancel);
                sent++;
            }

            var remote = await http.GetFromJsonAsync<CandidatesResponse>(
                $"api/sessions/{answer.Id}/candidates?since={received}", cancel);
            foreach (var c in remote?.Candidates ?? [])
            {
                AddRemoteCandidate(new IceCandidate(c.SdpMLineIndex, c.Candidate));
                received++;
            }

            await Task.Delay(200, cancel);
        }

        await http.DeleteAsync($"api/sessions/{answer.Id}", cancel);

        long buffers = Interlocked.Read(ref _buffers), loud = Interlocked.Read(ref _loudBuffers);
        Log.LogInformation(
            "Result: {Buffers} buffers back, {Loud} with the tone; candidates sent {Sent}, received {Received}; state {State}",
            buffers, loud, sent, received, ConnectionState);

        // 20 ms Opus frames: a few seconds of echo is well over 50 loud buffers.
        return loud >= 50 ? 0 : 1;
    }

    protected override void OnPadAdded(Pad pad)
    {
        if (Interlocked.Exchange(ref _branchBuilt, 1) != 0)
        {
            return;
        }

        using var branch = Global.ParseBinFromDescription(ReceiveBranch, ghostUnlinkedPads: true);
        Pipeline.Add(branch);
        var sinkPad = branch.GetByName("out")?.GetStaticPad("sink")
            ?? throw new InvalidOperationException("No fakesink pad.");

        // Also exercises the pad-probe API the media layer will need for dynamic add/remove.
        sinkPad.AddProbe(PadProbeType.Buffer, (_, info) =>
        {
            using var buffer = info.GetBuffer();
            if (buffer is not null)
            {
                Interlocked.Increment(ref _buffers);
                if (Peak(buffer) >= LoudThreshold)
                {
                    Interlocked.Increment(ref _loudBuffers);
                }
            }
            return PadProbeReturn.Ok;
        });

        branch.SyncStateWithParent();
        var linked = pad.Link(branch.GetStaticPad("sink")!);
        Log.LogInformation("Echo stream linked: {Result}", linked);
    }

    private static int Peak(Gst.Buffer buffer)
    {
        using var map = buffer.Map(MapFlags.Read);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(map.Span);
        int peak = 0;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs((int)s));
        }
        return peak;
    }

    protected override void DisposeCore() => _negotiationNeeded.Dispose();
}
