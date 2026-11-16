using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Tests;

/// <summary>Admin login (password → session cookie) and what the session and API keys may each reach.</summary>
public sealed class AdminAuthTests : IDisposable
{
    private const string Password = "correct horse battery";
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ServerFactory Server(IDictionary<string, string?>? settings = null) => new(_dir.File("server.db"), settings);

    private static Task<HttpResponseMessage> Login(HttpClient client, string password) =>
        client.PostAsJsonAsync("/api/admin/login", new { password });

    [Fact]
    public void Password_hashes_verify_and_reject()
    {
        var hash = AdminPassword.Hash(Password);

        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.True(AdminPassword.Verify(Password, hash));
        Assert.False(AdminPassword.Verify("wrong", hash));
        Assert.False(AdminPassword.Verify(Password, "garbage"));
        Assert.NotEqual(hash, AdminPassword.Hash(Password)); // salted
    }

    [Fact]
    public async Task Login_needs_a_password_to_be_set_and_the_right_one()
    {
        using var server = Server();
        var client = server.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict, (await Login(client, Password)).StatusCode); // none set yet
        server.Services.GetRequiredService<AdminPassword>().Set(Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "wrong")).StatusCode);
        Assert.False((await client.GetFromJsonAsync<AdminAuthentication.SessionResponse>("/api/admin/session"))!.LoggedIn);

        var ok = await Login(client, Password);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var cookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AdminAuthentication.CookieName + "="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.True((await client.GetFromJsonAsync<AdminAuthentication.SessionResponse>("/api/admin/session"))!.LoggedIn);
        Assert.Contains(server.Services.GetRequiredService<ActivityLog>().RecentEvents(), e => e.Kind == "admin.login");
    }

    [Fact]
    public async Task Changing_the_password_ends_every_session_made_with_the_old_one()
    {
        using var server = Server();
        var password = server.Services.GetRequiredService<AdminPassword>();
        password.Set(Password);
        var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Login(client, Password)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<AdminAuthentication.SessionResponse>("/api/admin/session"))!.LoggedIn);

        password.Set("a new password");

        Assert.False((await client.GetFromJsonAsync<AdminAuthentication.SessionResponse>("/api/admin/session"))!.LoggedIn);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/events")).StatusCode);
    }

    [Fact]
    public void Many_failures_from_anywhere_block_logins_for_a_while()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var throttle = new LoginThrottle(clock);
        for (var i = 0; i < LoginThrottle.MaxFailuresOverall; i++)
        {
            Assert.False(throttle.Blocked("fresh"));
            throttle.Failed($"10.0.{i / 250}.{i % 250}"); // a guesser spread over many addresses
        }
        Assert.True(throttle.Blocked("fresh"));
        clock.Now += LoginThrottle.Window;
        Assert.False(throttle.Blocked("fresh"));
    }

    [Fact]
    public async Task Admin_changes_from_another_site_are_refused()
    {
        using var server = Server();
        server.Services.GetRequiredService<AdminPassword>().Set(Password);
        var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Login(client, Password)).StatusCode);
        var room = await (await client.PostAsJsonAsync("/api/rooms", new { name = "mine" })).Read<RoomSnapshot>();

        HttpRequestMessage Delete(string origin) => new(HttpMethod.Delete, $"/api/rooms/{room.Id}") { Headers = { { "Origin", origin } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Delete("https://evil.localhost.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Delete("http://localhost"))).StatusCode); // its own page
    }

    [Fact]
    public async Task With_RequireHttps_admin_login_over_plain_http_is_refused()
    {
        using var server = Server(new Dictionary<string, string?> { ["Admin:RequireHttps"] = "true" });
        server.Services.GetRequiredService<AdminPassword>().Set(Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await Login(server.CreateClient(), Password)).StatusCode);
    }

    [Fact]
    public async Task RequireHttps_behind_a_reverse_proxy_works_only_when_the_proxy_is_trusted()
    {
        static HttpRequestMessage LoginFrom(string remote, params (string Name, string Value)[] headers)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/login")
            {
                Content = JsonContent.Create(new { password = Password }),
                Headers = { { TestRemoteAddress.Header, remote } },
            };
            foreach (var (name, value) in headers)
            {
                request.Headers.Add(name, value);
            }
            return request;
        }
        var throughProxy = new[] { ("X-Forwarded-For", "198.51.100.7"), ("X-Forwarded-Proto", "https") };

        // A proxy on the same host that is not trusted: its requests come from loopback, but are not "this machine".
        using (var untrusted = new ServerFactory(_dir.File("untrusted.db"), new Dictionary<string, string?> { ["Admin:RequireHttps"] = "true" }))
        {
            untrusted.Services.GetRequiredService<AdminPassword>().Set(Password);
            var client = untrusted.CreateClient();
            var refused = await client.SendAsync(LoginFrom("127.0.0.1", throughProxy));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("TrustedProxies", await refused.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(LoginFrom("127.0.0.1"))).StatusCode); // really local
        }

        // The same proxy, trusted: its X-Forwarded-Proto says HTTPS, and the client is the forwarded address.
        using var trusted = new ServerFactory(_dir.File("trusted.db"), new Dictionary<string, string?>
        {
            ["Admin:RequireHttps"] = "true",
            ["Server:TrustedProxies:0"] = "127.0.0.1",
            ["Admin:AllowedNetworks:0"] = "198.51.100.0/24",
        });
        trusted.Services.GetRequiredService<AdminPassword>().Set(Password);
        var viaProxy = trusted.CreateClient();
        var ok = await viaProxy.SendAsync(LoginFrom("127.0.0.1", throughProxy));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("secure", Assert.Single(ok.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        // The allow-list sees the forwarded client too: one outside it gets 404 through the same proxy.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viaProxy.SendAsync(LoginFrom("127.0.0.1", ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Proto", "https")))).StatusCode);
    }

    [Fact]
    public async Task An_admin_session_can_use_the_control_api_but_an_api_key_cannot_use_admin_endpoints()
    {
        using var server = Server();
        server.Services.GetRequiredService<AdminPassword>().Set(Password);
        var admin = server.CreateClient();
        var anonymous = server.CreateClient();
        var keyed = server.CreateAuthedClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/rooms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/events")).StatusCode);
        await Login(admin, Password);

        var created = await admin.PostAsJsonAsync("/api/rooms", new { name = "from the admin UI" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/api/rooms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyed.GetAsync("/api/admin/events")).StatusCode);
        var room = await created.Read<RoomSnapshot>();
        var events = await admin.GetFromJsonAsync<List<EventRecord>>($"/api/admin/events?room={room.Id}", Json.Options);
        Assert.Contains(events!, e => e.Kind == "room.created");
        Assert.All(events!, e => Assert.Equal(room.Id, e.RoomId));
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        using var server = Server();
        server.Services.GetRequiredService<AdminPassword>().Set(Password);
        var client = server.CreateClient();
        await Login(client, Password);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/admin/logout", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/events")).StatusCode);
    }

    [Fact]
    public async Task Repeated_failures_are_throttled()
    {
        using var server = Server();
        server.Services.GetRequiredService<AdminPassword>().Set(Password);
        var client = server.CreateClient();

        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "wrong")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(client, Password)).StatusCode); // even the right one
    }

    [Fact]
    public async Task A_configured_hash_wins_over_the_stored_password()
    {
        using var server = Server(new Dictionary<string, string?> { ["Admin:PasswordHash"] = AdminPassword.Hash("from config") });
        server.Services.GetRequiredService<AdminPassword>().Set(Password);
        var client = server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, "from config")).StatusCode);
    }
}
