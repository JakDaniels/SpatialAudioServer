using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Media;
using SpatialAudioServer.Media.Engines;

namespace SpatialAudioServer.Tests;

/// <summary>
/// What a Second Life viewer (Firestorm, indra/llwebrtc + indra/newview/llvoicewebrtc.cpp) does differently from a
/// browser: a data channel in the offer, munged SDP, and no trickle from the server (candidates must be in the answer).
/// </summary>
[Collection("media")]
public sealed class ViewerCompatibilityTests() : MediaTestBase(MixEngines.Default)
{
    [Fact]
    public async Task Viewer_style_clients_connect_and_mix_with_browser_style_ones()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient>
        {
            await JoinAsync(room.Id, 0, viewerStyle: true),
            await JoinAsync(room.Id, 1, viewerStyle: true),
            await JoinAsync(room.Id, 2),
        };

        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));

        var answer = clients[0].AnswerSdp!;
        Assert.Contains("a=candidate:", answer);
        Assert.Contains("a=end-of-candidates", answer);
        Assert.Contains("m=application", answer); // the SLData channel was negotiated
        Assert.Equal(0, clients[0].RemoteCandidates); // it never fetched any
    }
}

/// <summary>SDP handling, without media.</summary>
public sealed class SdpTests
{
    private const string Offer =
        "v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0 1\r\n" +
        "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=mid:0\r\n" +
        "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:1\r\na=rtpmap:111 opus/48000/2\r\na=fmtp:111 minptime=10;useinbandfec=1\r\n";

    [Fact]
    public void The_viewers_munging_is_reproduced_and_still_parses()
    {
        var munged = FakeClient.MungeLikeViewer(Offer);

        Assert.Contains("a=rtpmap:111 opus/48000/2\n", munged);
        Assert.Contains("a=fmtp:111 minptime=10;useinbandfec=1\ra=fmtp:111 minptime=10;useinbandfec=1;stereo=1", munged);
        Assert.Equal(111, WebRtcBin.OpusPayloadType(munged));
        Assert.Equal(1, WebRtcBin.FirstAudioMLine(munged)); // the data channel's m-line comes first in a viewer's offer
        Assert.Equal(2, WebRtcBin.SdpLines(munged).Count(l => l.StartsWith("a=fmtp:111", StringComparison.Ordinal)));
    }

    [Fact]
    public void Candidates_are_embedded_in_their_media_section()
    {
        IceCandidate[] candidates =
        [
            new(0, "candidate:1 1 UDP 2015363327 10.2.2.235 40000 typ host"),
            new(1, "candidate:2 1 UDP 2015363327 10.2.2.235 40001 typ host"),
        ];

        var lines = WebRtcBin.SdpLines(WebRtcBin.EmbedCandidates(Offer, candidates, complete: true)).ToList();

        var audio = lines.IndexOf("m=audio 9 UDP/TLS/RTP/SAVPF 111");
        var first = lines.IndexOf("a=candidate:1 1 UDP 2015363327 10.2.2.235 40000 typ host");
        var second = lines.IndexOf("a=candidate:2 1 UDP 2015363327 10.2.2.235 40001 typ host");
        Assert.InRange(first, lines.IndexOf("m=application 9 UDP/DTLS/SCTP webrtc-datachannel") + 1, audio - 1);
        Assert.True(second > audio);
        Assert.Equal(2, lines.Count(l => l == "a=end-of-candidates"));
    }
}
