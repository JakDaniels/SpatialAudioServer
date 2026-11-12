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
}
