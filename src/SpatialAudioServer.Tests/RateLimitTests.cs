using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SpatialAudioServer.Api;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>Signalling is rate-limited per participant and per client address; the control API is not.</summary>
public sealed class RateLimitTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Each_participant_has_its_own_budget()
    {
        using var server = new ServerFactory(_dir.File("server.db"), new Dictionary<string, string?>
        {
            ["Limits:SignalRequestsPerSecond"] = "2",
        });
        var (api, signal, first, second) = await SetUpAsync(server);

        var codes = await HammerAsync(signal, first, 10);
        Assert.Contains(HttpStatusCode.OK, codes);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.OK, (await ConfigAsync(signal, second)).StatusCode); // not affected

        using var limited = await ConfigAsync(signal, first);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));

        // The control API has no such limit.
        for (var i = 0; i < 20; i++)
        {
            (await api.GetAsync("/api/rooms")).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Participants_from_one_address_share_its_budget()
    {
        using var server = new ServerFactory(_dir.File("server.db"), new Dictionary<string, string?>
        {
            ["Limits:SignalRequestsPerSecond"] = "0",
            ["Limits:SignalRequestsPerSecondPerAddress"] = "2",
        });
        var (_, signal, first, second) = await SetUpAsync(server);

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, await HammerAsync(signal, first, 4)); // bucket holds 4
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ConfigAsync(signal, second)).StatusCode);
    }

    private static async Task<(HttpClient Api, HttpClient Signal, JoinResponse First, JoinResponse Second)> SetUpAsync(ServerFactory server)
    {
        var api = server.CreateAuthedClient();
        var room = await (await api.PostAsJsonAsync("/api/rooms", new { name = "limits" })).Read<RoomSnapshot>();
        var first = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "a" })).Read<JoinResponse>();
        var second = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "b" })).Read<JoinResponse>();
        return (api, server.CreateClient(), first, second);
    }

    private static async Task<List<HttpStatusCode>> HammerAsync(HttpClient signal, JoinResponse join, int count)
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            using var response = await ConfigAsync(signal, join);
            codes.Add(response.StatusCode);
        }
        return codes;
    }

    private static Task<HttpResponseMessage> ConfigAsync(HttpClient signal, JoinResponse join)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/signal/{join.ParticipantId}/config");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", join.JoinToken);
        return signal.SendAsync(request);
    }
}
