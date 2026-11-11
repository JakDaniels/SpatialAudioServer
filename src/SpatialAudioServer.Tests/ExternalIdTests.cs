using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Tests;

/// <summary>Addressing rooms and participants by OpenSim UUIDs (region/parcel and avatar) via externalId.</summary>
public sealed class ExternalIdTests : IDisposable
{
    private const string Region = "5a9e3c1e-7d1b-4b8e-9b56-2f1c0e8d4a10";
    private const string Avatar = "c0ffee00-1234-4abc-8def-0123456789ab";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ServerFactory Server() => new(_dir.File("server.db"));

    [Fact]
    public async Task Room_external_ids_are_unique()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/rooms", new { name = "A", externalId = Region })).StatusCode);
        var duplicate = await client.PostAsJsonAsync("/api/rooms", new { name = "B", externalId = Region.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Put_by_external_id_creates_once_then_returns_the_existing_room()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();

        var first = await client.PutAsJsonAsync($"/api/rooms/by-external/{Region}", new { maxParticipants = 4 });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Read<RoomSnapshot>();
        Assert.Equal(Region, created.Name); // name defaults to the external id
        Assert.Equal(4, created.MaxParticipants);

        // Second call (e.g. region restart) returns the same room and ignores the body.
        var second = await client.PutAsJsonAsync($"/api/rooms/by-external/{Region}", new { name = "Other", maxParticipants = 8 });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var existing = await second.Read<RoomSnapshot>();
        Assert.Equal(created.Id, existing.Id);
        Assert.Equal(4, existing.MaxParticipants);

        // No body at all is fine too.
        var bare = await client.PutAsync($"/api/rooms/by-external/{Guid.NewGuid()}", null);
        Assert.Equal(HttpStatusCode.Created, bare.StatusCode);
    }

    [Fact]
    public async Task Guid_external_ids_are_normalised()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();
        await client.PutAsync($"/api/rooms/by-external/{{{Region.ToUpperInvariant()}}}", null);

        var details = await client.GetFromJsonAsync<RoomDetails>($"/api/rooms/by-external/{Region}", Json.Options);

        Assert.Equal(Region, details!.ExternalId);
    }

    [Fact]
    public async Task The_region_module_can_work_entirely_with_opensim_uuids()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();
        var room = $"/api/rooms/by-external/{Region}";
        var avatar = $"{room}/participants/by-external/{Avatar}";

        await client.PutAsync(room, null);
        var joined = await client.PostAsJsonAsync($"{room}/participants", new { displayName = "Avatar", externalId = Avatar });
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        var join = await joined.Read<JoinResponse>();
        Assert.Null(join.ReplacedParticipantId);

        var moved = await client.PatchAsJsonAsync(avatar, new { position = new { x = 128, y = 128, z = 25 } });
        Assert.Equal(new Vec3(128, 128, 25), (await moved.Read<ParticipantSnapshot>()).Position);
        Assert.Equal(join.ParticipantId, (await client.GetFromJsonAsync<ParticipantSnapshot>(avatar, Json.Options))!.Id);
        Assert.Single((await client.GetFromJsonAsync<List<ParticipantSnapshot>>($"{room}/participants", Json.Options))!);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(avatar)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(avatar)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(room)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(room)).StatusCode);
    }

    [Fact]
    public async Task Our_ids_and_external_ids_can_be_mixed()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();
        var room = await (await client.PutAsync($"/api/rooms/by-external/{Region}", null)).Read<RoomSnapshot>();
        var join = await (await client.PostAsJsonAsync($"/api/rooms/{room.Id}/participants",
            new { displayName = "Avatar", externalId = Avatar })).Read<JoinResponse>();

        var byOurRoomTheirAvatar = await client.GetFromJsonAsync<ParticipantSnapshot>(
            $"/api/rooms/{room.Id}/participants/by-external/{Avatar}", Json.Options);
        var byTheirRoomOurParticipant = await client.GetFromJsonAsync<ParticipantSnapshot>(
            $"/api/rooms/by-external/{Region}/participants/{join.ParticipantId}", Json.Options);

        Assert.Equal(join.ParticipantId, byOurRoomTheirAvatar!.Id);
        Assert.Equal(join.ParticipantId, byTheirRoomOurParticipant!.Id);
    }

    [Fact]
    public async Task Re_adding_the_same_avatar_replaces_the_old_participant_even_in_a_full_room()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();
        var room = $"/api/rooms/by-external/{Region}";
        await client.PutAsJsonAsync(room, new { maxParticipants = 1 });

        var first = await (await client.PostAsJsonAsync($"{room}/participants", new { displayName = "Avatar", externalId = Avatar })).Read<JoinResponse>();
        var again = await client.PostAsJsonAsync($"{room}/participants", new { displayName = "Avatar", externalId = Avatar });

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        var second = await again.Read<JoinResponse>();
        Assert.Equal(first.ParticipantId, second.ReplacedParticipantId);
        Assert.NotEqual(first.ParticipantId, second.ParticipantId);
        var p = Assert.Single((await client.GetFromJsonAsync<List<ParticipantSnapshot>>($"{room}/participants", Json.Options))!);
        Assert.Equal(second.ParticipantId, p.Id);

        // The old participant's token is dead and its session is closed as replaced.
        var services = server.Services;
        Assert.Null(services.GetRequiredService<JoinTokenStore>().Redeem(first.JoinToken));
        var sessions = services.GetRequiredService<ActivityLog>().Sessions(second.RoomId);
        Assert.Equal("replaced", sessions.Single(s => s.ParticipantId == first.ParticipantId).LeaveReason);
        Assert.Null(sessions.Single(s => s.ParticipantId == second.ParticipantId).LeftAt);

        // A different avatar still hits the room limit.
        var other = await client.PostAsJsonAsync($"{room}/participants", new { displayName = "Other", externalId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
    }

    [Fact]
    public async Task The_same_avatar_can_be_in_two_rooms_at_once()
    {
        using var server = Server();
        var client = server.CreateAuthedClient();
        var regionA = $"/api/rooms/by-external/{Guid.NewGuid()}";
        var regionB = $"/api/rooms/by-external/{Guid.NewGuid()}";
        await client.PutAsync(regionA, null);
        await client.PutAsync(regionB, null);

        var inA = await (await client.PostAsJsonAsync($"{regionA}/participants", new { displayName = "Avatar", externalId = Avatar })).Read<JoinResponse>();
        var inB = await (await client.PostAsJsonAsync($"{regionB}/participants", new { displayName = "Avatar", externalId = Avatar })).Read<JoinResponse>();

        Assert.Null(inB.ReplacedParticipantId);
        Assert.NotEqual(inA.ParticipantId, inB.ParticipantId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{regionA}/participants/by-external/{Avatar}")).StatusCode);
    }

    [Fact]
    public void Migration_2_rejects_duplicate_external_ids_in_the_database()
    {
        var db = new Database(_dir.File("db.sqlite"));
        var store = new RoomStore(db);
        store.Insert(new RoomRecord(Guid.NewGuid(), "A", Region, 8, null, DateTimeOffset.UtcNow));

        Assert.Equal(Migrations.LatestVersion, db.SchemaVersion);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            store.Insert(new RoomRecord(Guid.NewGuid(), "B", Region, 8, null, DateTimeOffset.UtcNow)));
        store.Insert(new RoomRecord(Guid.NewGuid(), "C", null, 8, null, DateTimeOffset.UtcNow)); // NULLs are fine
        store.Insert(new RoomRecord(Guid.NewGuid(), "D", null, 8, null, DateTimeOffset.UtcNow));
    }
}
