// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Media.Engines;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>
/// The acoustic engine's conversion of orientations into the element's yaw, pitch and roll (its mixing is covered by
/// the shared suites in EngineSuites.cs, where the plugin is installed).
/// </summary>
public sealed class AcousticEngineTests
{
    [Theory]
    [InlineData(0, 0, 0, 1, 0, 0, 0)]                     // identity: facing east (+x)
    [InlineData(0, 0, 1, 0, 180, 0, 0)]                   // facing west
    [InlineData(0, 0, 0.70710678, 0.70710678, 90, 0, 0)]  // turned left, facing north
    [InlineData(0, -0.25881905, 0, 0.96592583, 0, 30, 0)] // looking 30° up (a negative turn about +y)
    [InlineData(0.25881905, 0, 0, 0.96592583, 0, 0, 30)]  // right ear 30° down
    public void Quaternions_become_yaw_pitch_and_roll(double x, double y, double z, double w, double yaw, double pitch, double roll)
    {
        var (gotYaw, gotPitch, gotRoll) = AcousticEngine.Euler(new Quat(x, y, z, w));
        Assert.Equal(yaw, gotYaw, 1e-6);
        Assert.Equal(pitch, gotPitch, 1e-6);
        Assert.Equal(roll, gotRoll, 1e-6);
    }

    [Fact]
    public void No_orientation_faces_east() => Assert.Equal((0.0, 0.0, 0.0), AcousticEngine.Euler(null));
}

/// <summary>A room's acoustics through the control API (no media): set at creation, changed later, stored.</summary>
public sealed class RoomAcousticsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Rooms_choose_and_change_their_acoustics_and_keep_them()
    {
        var path = _dir.File("server.db");
        Guid id;
        using (var server = new ServerFactory(path))
        {
            var api = server.CreateAuthedClient();
            var status = await api.GetFromJsonAsync<JsonObject>("/api/status");
            Assert.Equal(["outdoor", "small-room", "hall", "cave"], status!["acoustics"]!.AsArray().Select(n => (string?)n));

            var plain = await (await api.PostAsJsonAsync("/api/rooms", new { name = "plain", spatial = true })).Read<RoomSnapshot>();
            Assert.Null(plain.Acoustics);
            var hall = await (await api.PutAsJsonAsync("/api/rooms/by-external/parcel:x", new { spatial = true, acoustics = "Hall" }))
                .Read<RoomSnapshot>();
            Assert.Equal("hall", hall.Acoustics);
            id = hall.Id;

            var cave = await (await api.PatchAsJsonAsync($"/api/rooms/{id}", new { acoustics = "cave" })).Read<RoomSnapshot>();
            Assert.Equal("cave", cave.Acoustics);
            // A patch of something else leaves it alone.
            var still = await (await api.PatchAsJsonAsync($"/api/rooms/{id}", new { inWorldAvatars = "Optional" })).Read<RoomSnapshot>();
            Assert.Equal("cave", still.Acoustics);

            Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/rooms", new { name = "bad", acoustics = "cathedral" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsJsonAsync($"/api/rooms/{id}", new { acoustics = "cathedral" })).StatusCode);
        }
        using (var server = new ServerFactory(path))
        {
            var api = server.CreateAuthedClient();
            var room = await api.GetFromJsonAsync<JsonObject>($"/api/rooms/{id}");
            Assert.Equal("cave", (string?)room!["acoustics"]);
            // "outdoor" goes back to none.
            var outdoor = await (await api.PatchAsJsonAsync($"/api/rooms/{id}", new { acoustics = "outdoor" })).Read<RoomSnapshot>();
            Assert.Null(outdoor.Acoustics);
        }
    }
}

/// <summary>A running acoustic room takes a change of acoustics at once (the element's <c>room</c>).</summary>
[Collection("media")]
public sealed class AcousticRoomMediaTests() : MediaTestBase(AcousticEngine.Name)
{
    [SkippableFact]
    public async Task Changing_the_acoustics_sets_the_elements_room()
    {
        var room = await CreateRoomAsync(spatial: true);
        FakeClient[] clients = [await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1)];
        await SettleAndMeasureAsync(clients, Others(clients));
        Assert.StartsWith("Outdoor", await RoomPropertyAsync(room.Id));

        (await Api.PatchAsJsonAsync($"/api/rooms/{room.Id}", new { acoustics = "hall" })).EnsureSuccessStatusCode();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string value;
        while (!(value = await RoomPropertyAsync(room.Id)).StartsWith("Hall", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.StartsWith("Hall", value);
        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));
    }

    private async Task<string> RoomPropertyAsync(Guid roomId)
    {
        var graph = (await Media.PipelineGraphAsync(roomId, new HashSet<string>()))!;
        var mixer = graph.Elements.Single(e => e.Factory == AcousticEngine.Element);
        var details = (await Media.ElementDetailsAsync(roomId, mixer.Path))!;
        return details.Properties.Single(p => p.Name == "room").Value;
    }
}
