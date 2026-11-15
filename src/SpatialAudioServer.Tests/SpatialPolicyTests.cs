using System.Net;
using System.Net.Http.Json;
using SpatialAudioServer.Api;
using SpatialAudioServer.Media.Spatial;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>The default distance/direction model, the stereo pan law and the cluster grid (no media).</summary>
public sealed class SpatialPolicyTests
{
    private static readonly SpatialOptions Options = new(); // reference 10 m, max range 60 m
    private static readonly DistancePolicy Policy = new(Options);
    private static readonly Vec3 Origin = new(0, 0, 0);
    private static readonly Quat FacingEast = new(0, 0, 0, 1); // SL: identity rotation faces +x
    private static readonly Quat FacingWest = new(0, 0, 1, 0); // 180° about z
    private static readonly Quat FacingNorth = new(0, 0, Math.Sin(Math.PI / 4), Math.Cos(Math.PI / 4)); // 90° left about z

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(10, 1.0)]  // within the reference distance: full volume
    [InlineData(20, 0.5)]  // reference / distance
    [InlineData(40, 0.25)]
    [InlineData(60, 1.0 / 6)]
    public void Gain_falls_off_inversely_beyond_the_reference_distance(double distance, double gain)
    {
        Assert.Equal(gain, Policy.Render(new Vec3(distance, 0, 0), Origin, FacingEast).Gain, 6);
    }

    [Theory]
    [InlineData(DistanceModel.Inverse, 2, 20, 1.0 / 3)]        // 10 / (10 + 2 × 10)
    [InlineData(DistanceModel.Linear, 1, 35, 0.5)]              // half way from 10 m to 60 m
    [InlineData(DistanceModel.Linear, 2, 20, 0.6)]              // OpenSim's Vivox settings
    [InlineData(DistanceModel.Linear, 2, 35, 0.0)]              // … silent from 35 m
    [InlineData(DistanceModel.Exponential, 2, 20, 0.25)]        // (20 / 10)^−2
    public void Distance_models_and_rolloff(DistanceModel model, double rolloff, double distance, double gain)
    {
        var policy = new DistancePolicy(new SpatialOptions { DistanceModel = model, Rolloff = rolloff });
        var rendering = policy.Render(new Vec3(distance, 0, 0), Origin, FacingEast);
        Assert.Equal(gain, rendering.Gain, 6);
        Assert.Equal(gain == 0, rendering.IsSilent);
        Assert.Equal(1, policy.Render(new Vec3(10, 0, 0), Origin, FacingEast).Gain, 6);
    }

    [Fact]
    public void Sources_beyond_the_maximum_range_are_silent()
    {
        Assert.True(Policy.Render(new Vec3(60.01, 0, 0), Origin, FacingEast).IsSilent);
        Assert.True(Policy.Render(new Vec3(0, 0, 61), Origin, FacingEast).IsSilent); // height counts towards distance
    }

    [Fact]
    public void Pan_follows_the_listeners_facing()
    {
        var north = new Vec3(0, 5, 0);
        Assert.Equal(-1, Policy.Render(north, Origin, FacingEast).Pan, 6);  // facing east, north is on the left
        Assert.Equal(1, Policy.Render(north, Origin, FacingWest).Pan, 6);   // turned round: on the right
        Assert.Equal(0, Policy.Render(north, Origin, FacingNorth).Pan, 6);  // straight ahead
        Assert.Equal(0, Policy.Render(new Vec3(-5, 0, 0), Origin, FacingEast).Pan, 6); // behind: centred too
        Assert.Equal(-Math.Sqrt(0.5), Policy.Render(new Vec3(5, 5, 0), Origin, FacingEast).Pan, 6); // 45° left
    }

    [Fact]
    public void Positions_are_relative_to_the_listener()
    {
        var listener = new Vec3(128, 128, 22);
        var rendering = Policy.Render(new Vec3(128, 148, 22), listener, FacingEast); // 20 m north of them

        Assert.Equal(0.5, rendering.Gain, 6);
        Assert.Equal(-1, rendering.Pan, 6);
    }

    [Fact]
    public void Without_positions_everyone_is_centred_at_full_volume()
    {
        Assert.Equal(Rendering.Centre, Policy.Render(null, Origin, FacingEast));
        Assert.Equal(Rendering.Centre, Policy.Render(Origin, null, null));
        Assert.Equal(-1, Policy.Render(new Vec3(0, 5, 0), Origin, null).Pan, 6); // no orientation: faces east
    }

    [Theory]
    [InlineData(0, 1, 1)]      // centre: unity on both sides, as in a non-spatial room
    [InlineData(-1, 1, 0)]     // hard left
    [InlineData(1, 0, 1)]      // hard right
    public void Stereo_law_keeps_the_centre_at_unity_and_never_boosts(double pan, double left, double right)
    {
        var (l, r) = new Rendering(1, pan).Stereo();
        Assert.Equal(left, l, 6);
        Assert.Equal(right, r, 6);
        for (var p = -1.0; p <= 1; p += 0.125)
        {
            var (pl, pr) = new Rendering(1, p).Stereo();
            Assert.InRange(pl, 0, 1);
            Assert.InRange(pr, 0, 1);
        }
    }

    [Fact]
    public void Quantizer_rounds_to_the_grid_and_drops_near_silence()
    {
        var quantizer = new RenderingQuantizer(Options); // 3 dB, 8 pan steps per side

        Assert.Equal((0, 0), quantizer.Quantize(Rendering.Centre));
        Assert.Equal((-2, -8), quantizer.Quantize(new Rendering(0.5, -1))); // −6 dB, hard left
        Assert.Equal(quantizer.Quantize(new Rendering(0.5, 0.30)), quantizer.Quantize(new Rendering(0.52, 0.27))); // same cell
        Assert.Null(quantizer.Quantize(new Rendering(0.005, 0)));
        Assert.Null(quantizer.Quantize(Rendering.Silent));
        var back = quantizer.Dequantize((-2, 4));
        Assert.Equal(0.501, back.Gain, 3);
        Assert.Equal(0.5, back.Pan, 6);
    }
}

/// <summary>The per-room spatial flag through the control API.</summary>
public sealed class SpatialRoomTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Rooms_are_not_spatial_unless_asked_and_the_flag_persists()
    {
        RoomSnapshot spatial, plain;
        using (var server = new ServerFactory(_dir.File("server.db")))
        {
            var api = server.CreateAuthedClient();
            spatial = await (await api.PostAsJsonAsync("/api/rooms", new { name = "region", spatial = true })).Read<RoomSnapshot>();
            plain = await (await api.PostAsJsonAsync("/api/rooms", new { name = "meeting" })).Read<RoomSnapshot>();
            Assert.True(spatial.Spatial);
            Assert.False(plain.Spatial);
            var put = await api.PutAsJsonAsync("/api/rooms/by-external/region-2", new { spatial = true });
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            Assert.True((await put.Read<RoomSnapshot>()).Spatial);
        }

        using (var restarted = new ServerFactory(_dir.File("server.db")))
        {
            var api = restarted.CreateAuthedClient();
            Assert.True((await api.GetFromJsonAsync<RoomDetails>($"/api/rooms/{spatial.Id}", Json.Options))!.Spatial);
            Assert.False((await api.GetFromJsonAsync<RoomDetails>($"/api/rooms/{plain.Id}", Json.Options))!.Spatial);
        }
    }
}
