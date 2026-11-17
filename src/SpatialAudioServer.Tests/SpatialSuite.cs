// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>
/// Spatial rooms end to end: tones from positioned sources must arrive panned by direction and faded by distance,
/// and follow the listener turning and sources moving. Runs per engine (EngineSuites.cs).
/// </summary>
public abstract class SpatialSuite<TEngine>() : MediaTestBase(TEngine.Name) where TEngine : IEngineUnderTest
{
    protected static readonly Vec3 Origin = new(0, 0, 0);
    protected static readonly Quat FacingEast = new(0, 0, 0, 1);
    protected static readonly Quat FacingWest = new(0, 0, 1, 0);

    [Fact]
    public async Task Sources_are_panned_and_faded_by_position_and_follow_changes()
    {
        var room = await CreateRoomAsync(spatial: true);
        // The listener faces east (+x), so north (+y) is on its left.
        var listener = await JoinAsync(room.Id, 0, talking: false, position: Origin, orientation: FacingEast);
        var left = await JoinAsync(room.Id, 1, position: new Vec3(0, 5, 0));
        var right = await JoinAsync(room.Id, 2, position: new Vec3(0, -5, 0));
        var ahead = await JoinAsync(room.Id, 3, position: new Vec3(20, 0, 0));   // 20 m: half the level
        var far = await JoinAsync(room.Id, 4, position: new Vec3(100, 0, 0));    // beyond the 60 m range

        await SettleAndMeasureAsync([listener], _ => [left.ToneHz, right.ToneHz, ahead.ToneHz]);
        var levels = listener.Meter.Levels();
        AssertSide(levels[left.ToneHz], leftSide: true, "north source while facing east");
        AssertSide(levels[right.ToneHz], leftSide: false, "south source while facing east");
        AssertCentred(levels[ahead.ToneHz], "source straight ahead");
        AssertRatio(levels[ahead.ToneHz].Left, levels[left.ToneHz].Left, 0.5, "20 m vs 5 m");

        // Turn round, and bring the far source to 5 m behind.
        await Patch(room.Id, listener.ParticipantId, new { orientation = FacingWest });
        await Patch(room.Id, far.ParticipantId, new { position = new Vec3(5, 0, 0) });

        await SettleAndMeasureAsync([listener], _ => [left.ToneHz, right.ToneHz, ahead.ToneHz, far.ToneHz]);
        levels = listener.Meter.Levels();
        AssertSide(levels[left.ToneHz], leftSide: false, "north source while facing west");
        AssertSide(levels[right.ToneHz], leftSide: true, "south source while facing west");
        AssertCentred(levels[far.ToneHz], "source behind");
        AssertRatio(levels[far.ToneHz].Left, levels[left.ToneHz].Right, 1, "both within the reference distance");
    }

    [Fact]
    public async Task A_listener_can_turn_itself_through_signalling()
    {
        var room = await CreateRoomAsync(spatial: true); // clientPose defaults to orientation
        var listener = await JoinAsync(room.Id, 0, talking: false, position: Origin, orientation: FacingEast);
        var north = await JoinAsync(room.Id, 1, position: new Vec3(0, 5, 0));
        await SettleAndMeasureAsync([listener], _ => [north.ToneHz]);
        AssertSide(listener.Meter.Levels()[north.ToneHz], leftSide: true, "north source while facing east");

        await listener.SetListenerOrientationAsync(Signal, FacingWest); // its own session token, no API key

        await SettleAndMeasureAsync([listener], _ => [north.ToneHz]);
        AssertSide(listener.Meter.Levels()[north.ToneHz], leftSide: false, "north source after turning to face west");
    }

    [Fact]
    public async Task Participants_without_a_position_are_heard_centred_at_full_volume()
    {
        var room = await CreateRoomAsync(spatial: true);
        var listener = await JoinAsync(room.Id, 0, talking: false, position: Origin, orientation: FacingEast);
        var placed = await JoinAsync(room.Id, 1, position: new Vec3(0, 5, 0));
        var unplaced = await JoinAsync(room.Id, 2);

        await SettleAndMeasureAsync([listener], _ => [placed.ToneHz, unplaced.ToneHz]);
        var levels = listener.Meter.Levels();
        AssertCentred(levels[unplaced.ToneHz], "source without a position");
        AssertRatio(levels[unplaced.ToneHz].Left, levels[placed.ToneHz].Left, 1, "unplaced vs placed at 5 m");
    }

    protected Task Patch(Guid roomId, Guid participantId, object body) =>
        Api.PatchAsJsonAsync($"/api/rooms/{roomId}/participants/{participantId}", body).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    /// <summary>Hard to one side: the far channel well under a fifth of the near one.</summary>
    protected static void AssertSide((double Left, double Right) level, bool leftSide, string what)
    {
        var (near, other) = leftSide ? (level.Left, level.Right) : (level.Right, level.Left);
        Assert.True(near > 0.05 && other < near / 5,
            $"{what}: expected on the {(leftSide ? "left" : "right")}, got L {level.Left:F3} R {level.Right:F3}");
    }

    protected static void AssertCentred((double Left, double Right) level, string what) =>
        Assert.True(level.Left > 0.02 && Math.Abs(level.Left - level.Right) < 0.2 * Math.Max(level.Left, level.Right),
            $"{what}: expected centred, got L {level.Left:F3} R {level.Right:F3}");

    protected static void AssertRatio(double a, double b, double expected, string what) =>
        Assert.True(Math.Abs(a / b - expected) < 0.15 * Math.Max(1, expected),
            $"{what}: level ratio {a / b:F2}, expected {expected:F2} ({a:F3} / {b:F3})");
}
