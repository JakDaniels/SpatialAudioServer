// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.Hosting;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Tests;

public sealed class ControlApiTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ServerFactory Server(IDictionary<string, string?>? settings = null) => new(_dir.File("server.db"), settings);

    [Fact]
    public async Task Requests_without_a_valid_key_are_rejected()
    {
        using var server = Server();

        var anonymous = await server.CreateClient().GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var wrong = await server.CreateAuthedClient("wrong").GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var bearer = server.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", ServerFactory.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task Status_reports_counts_and_that_media_is_disabled()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();
        await client.PostAsJsonAsync("/api/rooms", new { name = "A" });

        var status = await (await client.GetAsync("/api/status")).Read<StatusResponse>();

        Assert.Equal(1, status.Rooms);
        Assert.Equal(0, status.Participants);
        Assert.False(status.Gstreamer.Enabled);
        Assert.True(status.Gstreamer.Ok);
        Assert.Equal(16, status.Limits.MaxRooms);
    }

    [Fact]
    public async Task Rooms_can_be_created_listed_found_by_external_id_and_deleted()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();

        var created = await client.PostAsJsonAsync("/api/rooms",
            new { name = "Region A", externalId = "region-uuid-a", maxParticipants = 10, metadata = new { owner = "x" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = await created.Read<RoomSnapshot>();
        Assert.Equal($"/api/rooms/{room.Id}", created.Headers.Location?.ToString());
        Assert.Equal(10, room.MaxParticipants);
        Assert.Equal("x", room.Metadata?["owner"]?.GetValue<string>());

        await client.PostAsJsonAsync("/api/rooms", new { name = "Region B", externalId = "region-uuid-b" });

        Assert.Equal(2, (await client.GetFromJsonAsync<List<RoomSnapshot>>("/api/rooms", Json.Options))!.Count);
        var byExternal = await client.GetFromJsonAsync<List<RoomSnapshot>>("/api/rooms?externalId=region-uuid-a", Json.Options);
        Assert.Equal(room.Id, Assert.Single(byExternal!).Id);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/rooms/{room.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/rooms/{room.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/rooms/{room.Id}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_input_returns_validation_problems()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();

        var response = await client.PostAsJsonAsync("/api/rooms", new { name = " ", maxParticipants = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Read<ValidationProblemDetails>();
        Assert.Contains("name", problem.Errors.Keys);
        Assert.Contains("maxParticipants", problem.Errors.Keys);
    }

    [Fact]
    public async Task Room_and_participant_limits_return_conflict()
    {
        using var server = Server(new Dictionary<string, string?> { ["Limits:MaxRooms"] = "1" });
        var client = server.CreateAuthedClient();

        var room = await (await client.PostAsJsonAsync("/api/rooms", new { name = "A", maxParticipants = 1 })).Read<RoomSnapshot>();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/rooms", new { name = "B" })).StatusCode);

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "One" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "Two" })).StatusCode);
    }

    [Fact]
    public async Task Participants_join_get_a_token_can_be_updated_and_removed()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();
        var room = await (await client.PostAsJsonAsync("/api/rooms", new { name = "A" })).Read<RoomSnapshot>();

        var joined = await client.PostAsJsonAsync($"/api/rooms/{room.Id}/participants",
            new { displayName = "Avatar One", externalId = "avatar-uuid", position = new { x = 1, y = 2, z = 3 } });
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        var join = await joined.Read<JoinResponse>();
        Assert.Equal($"/signal/{join.ParticipantId}", join.SignalUrl);
        Assert.False(string.IsNullOrEmpty(join.JoinToken));
        Assert.True(join.ExpiresAt > DateTimeOffset.UtcNow);

        // The token is the one the signalling endpoints will redeem (Phase 3).
        var claim = server.Services.GetRequiredService<JoinTokenStore>().Redeem(join.JoinToken);
        Assert.Equal(new JoinTokenClaim(join.ParticipantId, room.Id), claim);

        var details = await client.GetFromJsonAsync<RoomDetails>($"/api/rooms/{room.Id}", Json.Options);
        var p = Assert.Single(details!.Participants);
        Assert.Equal(ParticipantState.AwaitingSignal, p.State);
        Assert.Equal(new Vec3(1, 2, 3), p.Position);

        var patched = await client.PatchAsJsonAsync($"/api/rooms/{room.Id}/participants/{join.ParticipantId}",
            new { muted = true, gain = 0.5, orientation = new { x = 0, y = 0, z = 0, w = 1 } });
        var updated = await patched.Read<ParticipantSnapshot>();
        Assert.True(updated.Muted);
        Assert.Equal(0.5, updated.Gain);
        Assert.Equal(new Vec3(1, 2, 3), updated.Position); // untouched fields stay
        Assert.Equal(new Quat(0, 0, 0, 1), updated.Orientation);

        var badGain = await client.PatchAsJsonAsync($"/api/rooms/{room.Id}/participants/{join.ParticipantId}", new { gain = 9 });
        Assert.Equal(HttpStatusCode.BadRequest, badGain.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/rooms/{room.Id}/participants/{join.ParticipantId}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<ParticipantSnapshot>>($"/api/rooms/{room.Id}/participants", Json.Options))!);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/rooms/{room.Id}/participants/{join.ParticipantId}")).StatusCode);

        var session = Assert.Single(server.Services.GetRequiredService<ActivityLog>().Sessions(room.Id));
        Assert.Equal("removed", session.LeaveReason);
    }

    [Fact]
    public async Task Rooms_survive_a_restart_but_participants_do_not()
    {
        Guid roomId;
        using (var first = Server())
        {
            var client = first.CreateAuthedClient();
            var room = await (await client.PostAsJsonAsync("/api/rooms", new { name = "Persistent", externalId = "r1" })).Read<RoomSnapshot>();
            roomId = room.Id;
            await client.PostAsJsonAsync($"/api/rooms/{roomId}/participants", new { displayName = "Gone after restart" });
        }

        using var second = Server();
        var details = await second.CreateAuthedClient().GetFromJsonAsync<RoomDetails>($"/api/rooms/{roomId}", Json.Options);

        Assert.Equal("Persistent", details!.Name);
        Assert.Equal("r1", details.ExternalId);
        Assert.Empty(details.Participants);
        var session = Assert.Single(second.Services.GetRequiredService<ActivityLog>().Sessions(roomId));
        // A clean stop closes sessions itself; "server-restart" is left for sessions a crashed run left open.
        Assert.Equal("server-shutdown", session.LeaveReason);
    }

    [Fact]
    public async Task Deleting_a_room_ends_its_participants_sessions()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();
        var room = await (await client.PostAsJsonAsync("/api/rooms", new { name = "A" })).Read<RoomSnapshot>();
        await client.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "P" });

        await client.DeleteAsync($"/api/rooms/{room.Id}");

        var log = server.Services.GetRequiredService<ActivityLog>();
        Assert.Equal("room-deleted", Assert.Single(log.Sessions(room.Id)).LeaveReason);
        Assert.Contains(log.RecentEvents(), e => e.Kind == "room.deleted" && e.RoomId == room.Id);
    }

    [Fact]
    public async Task Stored_api_keys_work_until_revoked_via_the_cli()
    {
        using var server = Server();
        var output = new StringWriter();

        Assert.Equal(0, ApiKeyCommand.Run(server.Services, ["create", "region", "module"], output));
        var key = output.ToString().Split('\n', StringSplitOptions.TrimEntries).Single(l => l.StartsWith(ApiKeyStore.KeyPrefix));

        Assert.Equal(HttpStatusCode.OK, (await server.CreateAuthedClient(key).GetAsync("/api/status")).StatusCode);

        Assert.Equal(0, ApiKeyCommand.Run(server.Services, ["revoke", key[..12]], output));
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateAuthedClient(key).GetAsync("/api/status")).StatusCode);
    }
}

/// <summary>The --delete-existing-rooms start-up switch (testing: start with a clean, idle server).</summary>
public sealed class DeleteExistingRoomsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Stored_rooms_are_deleted_at_start_up_when_asked()
    {
        using (var server = new ServerFactory(_dir.File("server.db")))
        {
            var api = server.CreateAuthedClient();
            await api.PostAsJsonAsync("/api/rooms", new { name = "one" });
            await api.PostAsJsonAsync("/api/rooms", new { name = "two" });
        }

        using (var kept = new ServerFactory(_dir.File("server.db")))
        {
            Assert.Equal(2, (await kept.CreateAuthedClient().GetFromJsonAsync<List<RoomSnapshot>>("/api/rooms", Json.Options))!.Count);
        }

        using var cleaned = new ServerFactory(_dir.File("server.db")).WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton(new StartupFlags(DeleteExistingRooms: true))));
        var client = cleaned.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ServerFactory.ApiKey);
        Assert.Empty((await client.GetFromJsonAsync<List<RoomSnapshot>>("/api/rooms", Json.Options))!);
        Assert.Empty(cleaned.Services.GetRequiredService<RoomStore>().All()); // gone from the database, not just hidden
        Assert.Equal(2, cleaned.Services.GetRequiredService<ActivityLog>().RecentEvents()
            .Count(e => e.Kind == "room.deleted" && e.Detail!.Contains("--delete-existing-rooms")));
    }
}
