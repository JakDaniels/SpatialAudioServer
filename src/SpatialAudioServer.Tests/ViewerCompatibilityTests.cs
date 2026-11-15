using System.Net.Http.Json;
using SpatialAudioServer.Rooms;
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

    [Fact]
    public async Task Viewers_hear_who_joins_talks_and_leaves_over_their_data_channel()
    {
        var room = await CreateRoomAsync(spatial: true);
        var avatarA = Guid.NewGuid().ToString();
        var avatarB = Guid.NewGuid().ToString();
        var a = await JoinAsync(room.Id, 0, viewerStyle: true, talking: false, externalId: avatarA);
        var browser = await JoinAsync(room.Id, 1); // talking, no data channel: keyed by its participant id
        await a.DataChannelOpen.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(a.SendData("""{"j":{"p":true}}"""));

        // On its join: everyone announced so far (itself included) and the server's version under its own UUID.
        var welcome = await Until(a, m => m.ContainsKey(avatarA) && m[avatarA]!["V"] is not null);
        Assert.StartsWith("SpatialAudioServer ", (string)welcome[avatarA]!["V"]!);
        Assert.True((bool)welcome[avatarA]!["j"]!["p"]!);

        // A browser (no avatar) is announced a moment after it connects, so the simulator can register its name first.
        var browserJoin = await Until(a, m => m[browser.ParticipantId.ToString()]?["j"] is not null);
        Assert.True((bool)browserJoin[browser.ParticipantId.ToString()]!["j"]!["p"]!);

        // Someone joining later is announced; a talker's level and speaking flag follow.
        var b = await JoinAsync(room.Id, 2, viewerStyle: true, talking: false, externalId: avatarB);
        await b.DataChannelOpen.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(b.SendData("""{"j":{"p":true}}"""));
        await Until(a, m => m[avatarB]?["j"] is not null);
        var talking = await Until(a, m => m[browser.ParticipantId.ToString()]?["v"] is { } v && (bool)v!);
        Assert.InRange((int)talking[browser.ParticipantId.ToString()]!["p"]!, 1, 128);

        // Leaving.
        (await Api.DeleteAsync($"/api/rooms/{room.Id}/participants/{browser.ParticipantId}")).EnsureSuccessStatusCode();
        await Until(a, m => m[browser.ParticipantId.ToString()]?["l"] is { } l && (bool)l!);
        Assert.Contains(b.DataMessages, m => m.Contains("\"V\""));
    }

    [Fact]
    public async Task An_avatar_is_announced_only_while_this_is_its_primary_region()
    {
        var room = await CreateRoomAsync(spatial: true);
        var avatarA = Guid.NewGuid().ToString();
        var avatarB = Guid.NewGuid().ToString();
        var a = await JoinAsync(room.Id, 0, viewerStyle: true, talking: false, externalId: avatarA);
        var b = await JoinAsync(room.Id, 1, viewerStyle: true, talking: false, externalId: avatarB);
        await Task.WhenAll(a.DataChannelOpen, b.DataChannelOpen).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(a.SendData("""{"j":{"p":true}}"""));
        await Until(a, m => m[avatarA]?["V"] is not null);

        // B's avatar is in the neighbouring region: it listens here but is not in anyone's list for this region.
        Assert.True(b.SendData("""{"j":{}}"""));
        var welcome = await Until(b, m => m[avatarB]?["V"] is not null);
        Assert.Null(welcome[avatarB]!["j"]);
        Assert.True((bool)welcome[avatarA]!["j"]!["p"]!);
        await Task.Delay(ViewerData.GuestAnnounceDelay + ViewerData.LevelInterval);
        Assert.DoesNotContain(a.DataMessages, m => m.Contains(avatarB));

        // B crosses into this region (announced, then again: see ViewerData's remarks), then back out.
        Assert.True(b.SendData("""{"j":{"p":true}}"""));
        await Until(a, m => m[avatarB]?["j"]?["p"] is { } p && (bool)p!);
        await Until(a, _ => a.DataMessages.Count(m => m.Contains(avatarB) && m.Contains("\"j\"")) >= 2);
        Assert.True(b.SendData("""{"j":{}}"""));
        await Until(a, m => m[avatarB]?["l"] is { } l && (bool)l!);
    }

    /// <summary>Waits for a data channel message (a JSON object keyed by avatar) that matches.</summary>
    private static async Task<System.Text.Json.Nodes.JsonObject> Until(FakeClient client, Func<System.Text.Json.Nodes.JsonObject, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var text in client.DataMessages)
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject message && match(message))
                {
                    return message;
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"{client.Name} never got the message. It got: {string.Join(" | ", client.DataMessages)}");
    }

    [Fact]
    public async Task A_viewers_pose_arrives_over_its_data_channel_in_a_tethered_room()
    {
        var room = await (await Api.PostAsJsonAsync("/api/rooms",
            new { name = "region", engine = Engine, spatial = true, clientPose = "tethered" })).Read<RoomSnapshot>();
        var viewer = await JoinAsync(room.Id, 0, viewerStyle: true, position: new Vec3(256131, 256126, 25));
        await viewer.DataChannelOpen.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(viewer.SendData("""{"j":{"p":true}}"""));
        Assert.True(viewer.SendViewerPose(new Vec3(256132, 256126, 26), new Quat(0, 0, 0, 1),
            new Vec3(256122, 256126, 28), new Quat(0, 0, 0, 1)));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        ClientPoseReport? report = null;
        while (report is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            report = (await Api.GetFromJsonAsync<ParticipantSnapshot>($"/api/rooms/{room.Id}/participants/{viewer.ParticipantId}", Json.Options))!.ClientReport;
        }
        Assert.NotNull(report);
        Assert.Equal(new Vec3(256132, 256126, 26), report.SpeakerPosition);
        Assert.Equal(new Vec3(256122, 256126, 28), report.ListenerPosition);
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
