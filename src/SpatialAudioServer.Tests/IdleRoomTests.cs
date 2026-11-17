// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using SpatialAudioServer.Media;

namespace SpatialAudioServer.Tests;

/// <summary>A room's pipeline runs only while it has participants (plus Media:IdleRoomTimeout after the last leaves).</summary>
[Collection("media")]
public sealed class IdleRoomTests() : MediaTestBase(
    SpatialAudioServer.Media.Engines.MixEngines.Default,
    new Dictionary<string, string?> { ["Media:IdleRoomTimeout"] = "00:00:01" })
{
    [Fact]
    public async Task Media_starts_with_the_first_participant_and_stops_after_the_room_has_been_empty()
    {
        var room = await CreateRoomAsync();
        Assert.False(Media.IsRunning(room.Id));
        var idle = await (await Api.GetAsync($"/api/rooms/{room.Id}/media")).Read<RoomMediaStats>();
        Assert.False(idle.Running);
        Assert.Equal(room.Engine, idle.Engine);
        Assert.Null(await Media.PipelineDotAsync(room.Id));

        var clients = new List<LoadTest.FakeClient> { await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1) };
        Assert.True(Media.IsRunning(room.Id));
        await SettleAndMeasureAsync(clients, Others(clients));
        Assert.True((await (await Api.GetAsync($"/api/rooms/{room.Id}/media")).Read<RoomMediaStats>()).Running);

        foreach (var client in clients)
        {
            await client.LeaveAsync(Signal);
        }
        Assert.True(Media.IsRunning(room.Id)); // kept for the idle timeout
        await WaitUntilAsync(() => !Media.IsRunning(room.Id), TimeSpan.FromSeconds(10));
        Assert.Equal(0, await Media.MixedPairsAsync(room.Id));

        // Starts again for the next people in.
        var again = new List<LoadTest.FakeClient> { await JoinAsync(room.Id, 2), await JoinAsync(room.Id, 3) };
        Assert.True(Media.IsRunning(room.Id));
        AssertHears(await SettleAndMeasureAsync(again, Others(again)), Others(again));
    }

    [Fact]
    public async Task Rejoining_within_the_timeout_keeps_the_pipeline()
    {
        var room = await CreateRoomAsync();
        var first = await JoinAsync(room.Id, 0);
        await first.LeaveAsync(Signal);
        var second = await JoinAsync(room.Id, 1); // well within 1 s
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.True(Media.IsRunning(room.Id));

        var third = await JoinAsync(room.Id, 2);
        var clients = new List<LoadTest.FakeClient> { second, third };
        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));
    }

    [Fact]
    public async Task Deleting_a_room_stops_its_media()
    {
        var room = await CreateRoomAsync();
        await JoinAsync(room.Id, 0);
        Assert.True(Media.IsRunning(room.Id));
        (await Api.DeleteAsync($"/api/rooms/{room.Id}")).EnsureSuccessStatusCode();
        await Assert.ThrowsAsync<Rooms.NotFoundException>(() => Media.MixedPairsAsync(room.Id));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(100);
        }
    }
}
