using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SpatialAudioServer.Api;
using SpatialAudioServer.Media.Engines;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Tests;

/// <summary>Choosing a room's mix engine through the API (media off: this is bookkeeping, not audio).</summary>
public sealed class EngineSelectionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ServerFactory Server(IDictionary<string, string?>? settings = null) => new(_dir.File("server.db"), settings);

    [Fact]
    public async Task Status_lists_the_engines_and_the_default()
    {
        using var server = Server();
        var status = await (await server.CreateAuthedClient().GetAsync("/api/status")).Read<StatusResponse>();

        Assert.Equal(MixEngines.Default, status.DefaultEngine);
        Assert.Contains(status.Engines, e => e.Name == "pairchains");
        Assert.Contains(status.Engines, e => e.Name == "matrix");
        Assert.All(status.Engines, e => Assert.False(string.IsNullOrWhiteSpace(e.Description)));
    }

    [Fact]
    public async Task A_room_uses_the_requested_engine_or_the_configured_default()
    {
        using var server = Server(new Dictionary<string, string?> { ["Media:Engine"] = "matrix" });
        var client = server.CreateAuthedClient();

        var chosen = await (await client.PostAsJsonAsync("/api/rooms", new { name = "A", engine = "pairchains" })).Read<RoomSnapshot>();
        var defaulted = await (await client.PostAsJsonAsync("/api/rooms", new { name = "B" })).Read<RoomSnapshot>();
        var ensured = await (await client.PutAsJsonAsync("/api/rooms/by-external/region-c", new { engine = "pairchains" })).Read<RoomSnapshot>();

        Assert.Equal("pairchains", chosen.Engine);
        Assert.Equal("matrix", defaulted.Engine);
        Assert.Equal("pairchains", ensured.Engine);
        var details = await client.GetFromJsonAsync<RoomDetails>($"/api/rooms/{chosen.Id}", Json.Options);
        Assert.Equal("pairchains", details!.Engine);
    }

    [Fact]
    public async Task An_unknown_engine_is_rejected()
    {
        using var server = Server();
        var response = await server.CreateAuthedClient().PostAsJsonAsync("/api/rooms", new { name = "A", engine = "nope" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Read<ValidationProblemDetails>();
        Assert.Contains("engine", problem.Errors.Keys);
    }

    [Fact]
    public async Task The_engine_survives_a_restart_and_unknown_stored_engines_fall_back_to_the_default()
    {
        Guid roomId;
        using (var server = Server())
        {
            roomId = (await (await server.CreateAuthedClient().PostAsJsonAsync("/api/rooms", new { name = "A", engine = "matrix" }))
                .Read<RoomSnapshot>()).Id;
        }
        // A room stored by an older or newer server whose engine this one lacks.
        new RoomStore(new Database(_dir.File("server.db"))).Insert(
            new RoomRecord(Guid.NewGuid(), "Old", null, 8, null, DateTimeOffset.UtcNow, "retired-engine"));

        using var restarted = Server();
        var rooms = (await restarted.CreateAuthedClient().GetFromJsonAsync<List<RoomSnapshot>>("/api/rooms", Json.Options))!;

        Assert.Equal("matrix", rooms.Single(r => r.Id == roomId).Engine);
        Assert.Equal(MixEngines.Default, rooms.Single(r => r.Name == "Old").Engine);
    }

    [Fact]
    public void An_unknown_configured_default_stops_the_server_starting()
    {
        using var server = Server(new Dictionary<string, string?> { ["Media:Engine"] = "nope" });
        var error = Assert.ThrowsAny<Exception>(() => server.CreateClient());
        Assert.Contains("Media:Engine", error.ToString());
    }
}
