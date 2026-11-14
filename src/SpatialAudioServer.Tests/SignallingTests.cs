using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SpatialAudioServer.Api;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Media.Engines;
using SpatialAudioServer.Signalling;

namespace SpatialAudioServer.Tests;

/// <summary>Signalling authorisation. Engine-independent, so it runs once, on the default engine.</summary>
[Collection("media")]
public sealed class SignallingTests() : MediaTestBase(MixEngines.Default)
{
    [Fact]
    public async Task Signalling_requires_the_right_tokens()
    {
        var room = await CreateRoomAsync();
        var join = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "p" })).Read<JoinResponse>();
        var otherJoin = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "q" })).Read<JoinResponse>();

        Task<HttpResponseMessage> Send(HttpMethod method, string path, string? bearer, object? body = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            return Signal.SendAsync(request);
        }

        var offer = new OfferRequest("v=0\r\n");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", null, offer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", "nope", offer)).StatusCode);
        // Another participant's token does not work for this one (and is consumed by the attempt).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", otherJoin.JoinToken, offer)).StatusCode);
        // The control-API key is not a signalling credential.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", ServerFactory.ApiKey, offer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/signal/{join.ParticipantId}/ice", join.JoinToken)).StatusCode);

        // A real offer with the right token works once; the join token cannot be reused afterwards.
        using var client = new FakeClient("p", Tones[0], Tones);
        await client.ConnectAsync(Signal, join.ParticipantId, join.JoinToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", join.JoinToken, offer)).StatusCode);
    }

    [Fact]
    public async Task Oversized_signalling_input_is_refused()
    {
        var room = await CreateRoomAsync();
        var join = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "p" })).Read<JoinResponse>();
        async Task<HttpStatusCode> Send(string path, string bearer, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return (await Signal.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await Send($"/signal/{join.ParticipantId}/offer", join.JoinToken,
            new OfferRequest("v=0\r\n" + new string('a', SignallingApi.MaxSdpLength))));

        using var client = new FakeClient("p", Tones[0], Tones);
        await client.ConnectAsync(Signal, join.ParticipantId, join.JoinToken); // the refused offer did not use up the token
        var ice = $"/signal/{join.ParticipantId}/ice";
        var line = "candidate:1 1 udp 2122260223 192.0.2.1 50000 typ host";
        Assert.Equal(HttpStatusCode.BadRequest, await Send(ice, client.SessionToken!, new CandidatesRequest(
            Enumerable.Range(0, SignallingApi.MaxCandidatesPerRequest + 1).Select(_ => new CandidateDto(line, 0)).ToList(), null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Send(ice, client.SessionToken!, new CandidatesRequest(
            [new CandidateDto(line + new string(' ', SignallingApi.MaxCandidateLength), 0)], null)));

        // A connection takes at most MaxRemoteCandidates in all.
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < SpatialAudioServer.Media.WebRtcBin.MaxRemoteCandidates / SignallingApi.MaxCandidatesPerRequest + 1; i++)
        {
            codes.Add(await Send(ice, client.SessionToken!, new CandidatesRequest(Enumerable.Range(0, SignallingApi.MaxCandidatesPerRequest)
                .Select(n => new CandidateDto($"candidate:{i}{n} 1 udp 1 192.0.2.{n + 1} {40000 + i} typ host", 0)).ToList(), null)));
        }
        Assert.Equal(HttpStatusCode.Conflict, codes[^1]);
    }

    [Fact]
    public async Task Malformed_offers_fail_cleanly_and_the_room_keeps_working()
    {
        var room = await CreateRoomAsync();
        var join = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "p" })).Read<JoinResponse>();
        using var first = new FakeClient("p", Tones[0], Tones);
        await first.ConnectAsync(Signal, join.ParticipantId, join.JoinToken);

        string[] bad =
        [
            "garbage",
            "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=rtpmap:111 opus/48000/2\r\n", // no session lines, ICE or DTLS
            "v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 VP8/90000\r\n",
            "v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=rtpmap:111 opus/48000/2\r\n" +
                "a=fingerprint:sha-256 zz\r\na=setup:bogus\r\na=mid:\u0000\r\n",
        ];
        foreach (var sdp in bad)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer")
            {
                Content = JsonContent.Create(new OfferRequest(sdp)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first.SessionToken);
            using var response = await Signal.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Others can still join and hear each other.
        var clients = new List<FakeClient> { await JoinAsync(room.Id, 1), await JoinAsync(room.Id, 2) };
        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));
    }
}
