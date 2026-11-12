using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SpatialAudioServer.Api;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Signalling;

namespace SpatialAudioServer.Tests;

/// <summary>What the web client needs before it offers, and the static client itself (media off).</summary>
public sealed class ClientConfigTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Config_gives_the_ice_servers_without_using_up_the_join_token()
    {
        using var server = new ServerFactory(_dir.File("server.db"), new Dictionary<string, string?>
        {
            ["Media:IceServers:0:Urls:0"] = "stun:stun.example.org:3478",
            ["Media:IceServers:1:Urls:0"] = "turn:turn.example.org:3478?transport=udp",
            ["Media:IceServers:1:Username"] = "user",
            ["Media:IceServers:1:Credential"] = "secret",
        });
        var api = server.CreateAuthedClient();
        var room = await (await api.PostAsJsonAsync("/api/rooms", new { name = "A" })).Read<RoomSnapshot>();
        var join = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "p" })).Read<JoinResponse>();
        var client = server.CreateClient();

        async Task<HttpResponseMessage> Config(string? bearer)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/signal/{join.ParticipantId}/config");
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            return await client.SendAsync(request);
        }

        var config = await (await Config(join.JoinToken)).Read<ClientConfig>();
        Assert.Equal(["stun:stun.example.org:3478"], config.IceServers[0].Urls);
        Assert.Equal("user", config.IceServers[1].Username);
        Assert.Equal(HttpStatusCode.OK, (await Config(join.JoinToken)).StatusCode); // still valid: not consumed
        Assert.Equal(HttpStatusCode.Unauthorized, (await Config(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Config("nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Config(ServerFactory.ApiKey)).StatusCode);
    }

    [Fact]
    public async Task The_test_client_is_served_without_a_key()
    {
        using var server = new ServerFactory(_dir.File("server.db"));
        var client = server.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/client/", root.Headers.Location?.ToString());

        var page = await client.GetAsync("/client/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("client.js", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/client/client.js")).StatusCode);
    }
}
