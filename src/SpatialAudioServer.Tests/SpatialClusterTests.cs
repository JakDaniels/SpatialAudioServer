using System.Net.Http.Json;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>matrix-shared in a spatial room: listeners who hear the talkers the same way share one mix and encoder.</summary>
[Collection("media")]
public sealed class SpatialClusterTests() : MediaTestBase("matrix-shared")
{
    private static readonly Quat FacingEast = new(0, 0, 0, 1);
    private static readonly Quat FacingWest = new(0, 0, 1, 0);

    [Fact]
    public async Task Listeners_share_a_mix_per_rendering_and_regroup_when_they_turn()
    {
        var room = await CreateRoomAsync(spatial: true);
        var north = await JoinAsync(room.Id, 1, position: new Vec3(0, 5, 0));
        var south = await JoinAsync(room.Id, 2, position: new Vec3(0, -5, 0));
        // Two listeners half a metre apart facing east hear the talkers the same way (same grid cell); a third,
        // facing west, hears them mirrored.
        var east1 = await JoinAsync(room.Id, 0, talking: false, position: new Vec3(0, 0, 0), orientation: FacingEast);
        var east2 = await JoinAsync(room.Id, 3, talking: false, position: new Vec3(0.5, 0, 0), orientation: FacingEast);
        var west = await JoinAsync(room.Id, 4, talking: false, position: new Vec3(0, 0, 0), orientation: FacingWest);
        FakeClient[] all = [north, south, east1, east2, west];
        FakeClient[] talkers = [north, south];

        await SettleAndMeasureAsync(all, Others(talkers));
        await WaitForCounters(room.Id, clusters: 2, encoders: 4); // 2 clusters + 2 talkers' personal mixes
        AssertLeft(east1, north);
        AssertLeft(east2, north);
        AssertLeft(west, south);

        await Api.PatchAsJsonAsync($"/api/rooms/{room.Id}/participants/{west.ParticipantId}", new { orientation = FacingEast });

        await WaitForCounters(room.Id, clusters: 1, encoders: 3); // the west cluster empties and goes after its grace period
        await SettleAndMeasureAsync(all, Others(talkers));
        AssertLeft(west, north);
    }

    private static void AssertLeft(FakeClient listener, FakeClient talker)
    {
        var (left, right) = listener.Meter.Levels()[talker.ToneHz];
        Assert.True(left > 0.05 && right < left / 5, $"{listener.Name} should hear {talker.Name} on the left: L {left:F3} R {right:F3}");
    }

    private async Task WaitForCounters(Guid roomId, int clusters, int encoders)
    {
        IReadOnlyDictionary<string, long> counters = new Dictionary<string, long>();
        for (var deadline = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < deadline; await Task.Delay(200))
        {
            counters = await Media.CountersAsync(roomId);
            if (counters["clusters"] == clusters && counters["encoders"] == encoders)
            {
                return;
            }
        }
        Assert.Fail($"Expected {clusters} clusters and {encoders} encoders, have {counters["clusters"]} and {counters["encoders"]}.");
    }
}
