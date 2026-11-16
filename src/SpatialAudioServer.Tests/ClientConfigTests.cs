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
    public void Turn_credentials_from_a_secret_follow_the_turn_rest_scheme()
    {
        var server = new SpatialAudioServer.Configuration.IceServerConfig { Secret = "s3cret" };
        var (user, credential) = server.CredentialsFor("p1", new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        // expiry (now + 12 h) in Unix seconds, then base64(HMAC-SHA1(secret, username)), as coturn's use-auth-secret checks.
        Assert.Equal("1794787200:p1", user);
        Assert.Equal("y01NmWQ+E5UzHvmnrnAWa/QebKY=", credential);
        Assert.Equal(("u", "c"), new SpatialAudioServer.Configuration.IceServerConfig { Username = "u", Credential = "c" }
            .CredentialsFor("p1", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task With_a_turn_secret_each_participant_gets_its_own_credentials()
    {
        using var server = new ServerFactory(_dir.File("secret.db"), new Dictionary<string, string?>
        {
            ["Media:IceServers:0:Urls:0"] = "turns:turn.example.org:5349?transport=tcp",
            ["Media:IceServers:0:Secret"] = "s3cret",
        });
        var api = server.CreateAuthedClient();
        var room = await (await api.PostAsJsonAsync("/api/rooms", new { name = "A" })).Read<RoomSnapshot>();
        async Task<IceServerDto> TurnFor(string name)
        {
            var join = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = name })).Read<JoinResponse>();
            var client = server.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", join.JoinToken);
            var config = await client.GetFromJsonAsync<ClientConfig>($"/signal/{join.ParticipantId}/config", Json.Options);
            Assert.EndsWith(":" + join.ParticipantId, config!.IceServers[0].Username);
            return config.IceServers[0];
        }

        var a = await TurnFor("a");
        var b = await TurnFor("b");
        Assert.NotEqual(a.Credential, b.Credential);
        var expiry = long.Parse(a.Username!.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(DateTimeOffset.FromUnixTimeSeconds(expiry) - DateTimeOffset.UtcNow, TimeSpan.FromHours(11.9), TimeSpan.FromHours(12.1));
    }

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
