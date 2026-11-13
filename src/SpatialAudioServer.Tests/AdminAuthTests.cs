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
