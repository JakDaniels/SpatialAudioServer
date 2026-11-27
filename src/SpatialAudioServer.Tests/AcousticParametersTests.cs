// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Media.Engines;
using SpatialAudioServer.Media.Spatial;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>A room's acoustic parameters (#31): validation, merging, the element settings and reading values back.</summary>
public sealed class AcousticParametersTests
{
    private static (JsonObject? Merged, Dictionary<string, string> Errors) Merge(JsonObject? current, string changes)
    {
        var errors = new Dictionary<string, string>();
        var merged = AcousticParameters.Merge(current, JsonNode.Parse(changes)!.AsObject(), (f, m) => errors[f] = m);
        return (merged, errors);
    }

    [Fact]
    public void Values_are_checked_and_normalised()
    {
        var (merged, errors) = Merge(null, """
            { "reverbDecay": 1.6, "reflection-delays": "12, 20, 33", "WallMaterial": "Stone", "floorAbsorption": 0.3,
              "reverbLevel": "-3" }
            """);
        Assert.Empty(errors);
        Assert.Equal(1.6, (double)merged!["reverbDecay"]!);
        Assert.Equal([12.0, 20.0, 33.0], merged["reflectionDelays"]!.AsArray().Select(v => (double)v!));
        Assert.Equal("stone", (string?)merged["wallMaterial"]);
        Assert.Equal([0.3], merged["floorAbsorption"]!.AsArray().Select(v => (double)v!));
        Assert.Equal(-3.0, (double)merged["reverbLevel"]!);
    }

    [Fact]
    public void Bad_values_are_refused_by_key()
    {
        var (_, errors) = Merge(null, """
            { "reverbDecay": 30, "colour": "blue", "wallMaterial": "cheese", "reflectionLevels": [1, 2, "x"],
              "ceilingAbsorption": [0.1, 0.2, 0.3], "roomWidth": "wide" }
            """);
        Assert.Equal(
            ["acousticParameters.ceilingAbsorption", "acousticParameters.colour", "acousticParameters.reflectionLevels",
             "acousticParameters.reverbDecay", "acousticParameters.roomWidth", "acousticParameters.wallMaterial"],
            errors.Keys.Order());
    }

    [Fact]
    public void Null_removes_a_value_and_nothing_left_is_null()
    {
        var (first, _) = Merge(null, """{ "reverbDecay": 1.6, "roomWidth": 10 }""");
        var (second, _) = Merge(first, """{ "reverb-decay": null }""");
        Assert.Equal(["roomWidth"], second!.Select(p => p.Key));
        Assert.Null(Merge(second, """{ "roomWidth": null }""").Merged);
    }

    [Fact]
    public void Unset_values_leave_the_preset_and_the_rooms_curve_in_charge()
    {
        var parameters = JsonNode.Parse("""{ "reflectionDelays": [5, 10.5], "reverbDecay": 1.25 }""")!.AsObject();
        var curve = new SpatialOptions { DistanceModel = DistanceModel.Linear, ReferenceDistance = 10, MaxRange = 60 };
        var settings = AcousticParameters.ElementSettings(parameters, curve).ToDictionary(s => s.Property, s => s.Value);
        Assert.Equal(AcousticParameters.All.Count, settings.Count);
        Assert.Equal("< 5, 10.5 >", settings["reflection-delays"]);
        Assert.Equal("1.25", settings["reverb-decay"]);
        Assert.Equal("< >", settings["reflection-levels"]);
        Assert.Equal("-1", settings["reverb-size"]);
        Assert.Equal("linear", settings["distance-model"]);
        Assert.Equal("10", settings["reference-distance"]);
        Assert.Equal("0", settings["room-width"]);
        Assert.Equal("wood", settings["floor-material"]);
    }

    [Fact]
    public void Room_parameters_are_read_back_by_key()
    {
        // As acousticmultimixer 0.2.0 writes it.
        const string text = "room-parameters, room=(GstAcousticMultiMixerRoom)hall, reflection-delays=(double)< 5, 10.5 >, " +
            "reflection-levels=(double)< -17.056839373155615, -15.118639112994488 >, wall-cutoff=(double)0, room-falloff=(double)0.75, " +
            "reflection-spread=(double)0.080000000000000002, reflection-diffusion=(double)0.5, near-floor=(double)0.5, " +
            "overridden=(string)< reflection-delays >, derived=(string)< reflection-levels, wall-cutoff, reverb-decay >, " +
            "reverb-decay=(double)0.6014719373164098, reverb-treble-decay=(double)0.48642854417942666, reverb-size=(double)10.443409773290981, " +
            "reverb-room-level=(double)-10, wall-absorption=(double)< 0.02, 0.050000000000000003 >;";
        var read = new Dictionary<string, string> { ["reverb-level"] = "-3", ["distance-model"] = "linear", ["room-width"] = "10", ["floor-absorption"] = "< >" };
        var values = AcousticParameters.Values(text, p => read.GetValueOrDefault(p));
        Assert.Equal("hall", (string?)values["room"]);
        Assert.Equal([5.0, 10.5], values["reflectionDelays"]!.AsArray().Select(v => (double)v!));
        Assert.Equal(-17.0568, (double)values["reflectionLevels"]![0]!);
        Assert.Equal(0.6015, (double)values["reverbDecay"]!);
        Assert.Equal(-10.0, (double)values["reverbRoomLevel"]!);
        Assert.Equal(-3.0, (double)values["reverbLevel"]!);
        Assert.Equal("linear", (string?)values["distanceModel"]);
        Assert.Equal(10.0, (double)values["roomWidth"]!);
        Assert.Equal([0.02, 0.05], values["wallAbsorption"]!.AsArray().Select(v => (double)v!));
        Assert.Empty(values["floorAbsorption"]!.AsArray());
        Assert.Equal(["reflectionDelays"], values["overridden"]!.AsArray().Select(v => (string?)v));
        Assert.Equal(["reflectionLevels", "wallCutoff", "reverbDecay"], values["derived"]!.AsArray().Select(v => (string?)v));
    }

    [Fact]
    public void Without_a_reverb_its_values_are_null()
    {
        var values = AcousticParameters.Values("room-parameters, room=(GstAcousticMultiMixerRoom)outdoor, reflection-delays=(double)< >;", _ => null);
        Assert.Empty(values["reflectionDelays"]!.AsArray());
        Assert.Null(values["reverbDecay"]);
    }
}

/// <summary>A room's acoustic parameters through the control API (no media): set, merged, stored, refused.</summary>
public sealed class RoomAcousticParametersApiTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Rooms_set_merge_and_keep_their_parameters()
    {
        var path = _dir.File("server.db");
        Guid id;
        using (var server = new ServerFactory(path))
        {
            var api = server.CreateAuthedClient();
            var room = await (await api.PostAsJsonAsync("/api/rooms", new
            {
                name = "tuned", spatial = true, acoustics = "hall", acousticParameters = new { reverbDecay = 1.6 },
            })).Read<RoomSnapshot>();
            id = room.Id;
            Assert.Equal(1.6, (double)room.AcousticParameters!["reverbDecay"]!);

            var patched = await (await api.PatchAsJsonAsync($"/api/rooms/{id}",
                JsonNode.Parse("""{ "acousticParameters": { "room-width": 12, "reverbDecay": null, "wallMaterial": "stone" } }""")))
                .Read<RoomSnapshot>();
            Assert.Equal(["roomWidth", "wallMaterial"], patched.AcousticParameters!.Select(p => p.Key).Order());

            // One bad value refuses the whole change.
            var bad = await api.PatchAsJsonAsync($"/api/rooms/{id}",
                JsonNode.Parse("""{ "acousticParameters": { "roomHeight": 4, "reverbSize": 9999 } }"""));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("acousticParameters.reverbSize", await bad.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest,
                (await api.PostAsJsonAsync("/api/rooms", new { name = "bad", acousticParameters = new { nothing = 1 } })).StatusCode);

            // Without media there are no values, but the parameters are described.
            var acoustics = await api.GetFromJsonAsync<JsonObject>($"/api/rooms/{id}/acoustics");
            Assert.Equal("hall", (string?)acoustics!["acoustics"]);
            Assert.Null(acoustics["values"]);
            Assert.Contains(acoustics["parameters"]!.AsArray(), p => (string?)p!["key"] == "reverbDecay" && (string?)p["property"] == "reverb-decay");
        }
        using (var server = new ServerFactory(path))
        {
            var api = server.CreateAuthedClient();
            var room = await api.GetFromJsonAsync<JsonObject>($"/api/rooms/{id}");
            Assert.Equal(12.0, (double)room!["acousticParameters"]!["roomWidth"]!);
            Assert.Null(room["acousticParameters"]!["roomHeight"]);
        }
    }
}

/// <summary>A running acoustic room takes its parameters at once, and an idle room's values are previewed.</summary>
[Collection("media")]
public sealed class AcousticParametersMediaTests() : MediaTestBase(AcousticEngine.Name)
{
    [SkippableFact]
    public async Task A_running_room_takes_its_parameters_and_reports_them()
    {
        var room = await CreateRoomAsync(spatial: true);
        (await Api.PatchAsJsonAsync($"/api/rooms/{room.Id}", new { acoustics = "hall" })).EnsureSuccessStatusCode();
        FakeClient[] clients = [await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1)];
        await SettleAndMeasureAsync(clients, Others(clients));
        Skip.If(await ValuesAsync(room.Id) is not { } before || before["reverbRoomLevel"] is null,
            "The installed acousticmultimixer has no room parameters (0.2.0 or later).");

        (await Api.PatchAsJsonAsync($"/api/rooms/{room.Id}",
            JsonNode.Parse("""{ "acousticParameters": { "reverbDecay": 1.6, "reflectionDelays": [12, 20] } }"""))).EnsureSuccessStatusCode();
        var values = await WaitForAsync(room.Id, v => v["reverbDecay"]?.GetValue<double>() == 1.6);
        Assert.Equal([12.0, 20.0], values["reflectionDelays"]!.AsArray().Select(v => (double)v!));
        Assert.Equal(["reflectionDelays", "reverbDecay"], values["overridden"]!.AsArray().Select(v => (string?)v).Order());
        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));

        // Back to the preset's.
        (await Api.PatchAsJsonAsync($"/api/rooms/{room.Id}",
            JsonNode.Parse("""{ "acousticParameters": { "reverbDecay": null, "reflectionDelays": null } }"""))).EnsureSuccessStatusCode();
        values = await WaitForAsync(room.Id, v => v["reverbDecay"]?.GetValue<double>() == 1.1);
        Assert.Empty(values["overridden"]!.AsArray());
    }

    [SkippableFact]
    public async Task An_idle_rooms_values_come_from_its_settings()
    {
        var room = await CreateRoomAsync(spatial: true);
        (await Api.PatchAsJsonAsync($"/api/rooms/{room.Id}",
            JsonNode.Parse("""{ "acousticParameters": { "roomWidth": 10, "roomDepth": 8, "roomHeight": 3 } }"""))).EnsureSuccessStatusCode();
        var values = await ValuesAsync(room.Id);
        Skip.If(values?["reverbRoomLevel"] is null, "The installed acousticmultimixer has no room parameters (0.2.0 or later).");
        // Outdoors with a size is a room: its values derived from the dimensions.
        Assert.NotNull(values!["reverbDecay"]);
        Assert.Contains("reverbDecay", values["derived"]!.AsArray().Select(v => (string?)v));
        Assert.Equal(10.0, (double)values["roomWidth"]!);
    }

    private async Task<JsonObject?> ValuesAsync(Guid roomId) =>
        (await Api.GetFromJsonAsync<JsonObject>($"/api/rooms/{roomId}/acoustics"))!["values"] as JsonObject;

    private async Task<JsonObject> WaitForAsync(Guid roomId, Func<JsonObject, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        JsonObject? values;
        while ((values = await ValuesAsync(roomId)) is null || !done(values))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Values did not change: {values?.ToJsonString()}");
            await Task.Delay(50);
        }
        return values;
    }
}
