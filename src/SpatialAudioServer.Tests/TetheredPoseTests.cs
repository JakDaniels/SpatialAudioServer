// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.Media.Spatial;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>An SL viewer's SLData messages, as Firestorm's llvoicewebrtc.cpp writes them (no media).</summary>
public sealed class SlDataTests
{
    [Fact]
    public void A_pose_update_is_read_in_metres_with_unit_quaternions()
    {
        // Global coordinates in cm, quaternions ×100, as integers (sendPositionUpdate).
        var message = SlData.Parse(
            """{"sp":{"x":25613110,"y":25612621,"z":2679},"sh":{"x":0,"y":0,"z":70,"w":70},""" +
            """ "lp":{"x":25612500,"y":25612621,"z":2800},"lh":{"x":0,"y":0,"z":0,"w":100}}""")!;

        var pose = message.Pose!;
        Assert.Equal(new Vec3(256131.1, 256126.21, 26.79), pose.SpeakerPosition);
        Assert.Equal(new Vec3(256125, 256126.21, 28), pose.ListenerPosition);
        Assert.Equal(Math.Sqrt(0.5), pose.SpeakerOrientation.Z, 6); // normalised
        Assert.Equal(Math.Sqrt(0.5), pose.SpeakerOrientation.W, 6);
        Assert.Equal(new Quat(0, 0, 0, 1), pose.ListenerOrientation);
        Assert.False(message.Joined);
        Assert.Empty(message.OtherKeys);
    }

    [Fact]
    public void Join_mute_and_volume_messages_are_read()
    {
        var a = "a1b2c3d4-0000-0000-0000-000000000001";
        var b = "a1b2c3d4-0000-0000-0000-000000000002";

        var join = SlData.Parse("""{"j":{"p":true}}""")!;
        var secondary = SlData.Parse("""{"j":{}}""")!;
        var peers = SlData.Parse("{\"m\":{\"" + a + "\":true,\"" + b + "\":false},\"ug\":{\"" + a + "\":220}}")!;

        Assert.True(join.Joined);
        Assert.True(join.Primary);
        Assert.True(secondary.Joined);
        Assert.False(secondary.Primary);
        Assert.Null(join.Pose);
        Assert.Equal(new Dictionary<string, bool> { [a] = true, [b] = false }, peers.Mutes);
        Assert.Equal(new Dictionary<string, int> { [a] = 220 }, peers.Gains);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("42")]
    public void What_is_not_a_json_object_is_not_sldata(string text) => Assert.Null(SlData.Parse(text));

    [Fact]
    public void Unknown_keys_are_kept_and_incomplete_poses_dropped()
    {
        var message = SlData.Parse("""{"sp":{"x":1,"y":2,"z":3},"sh":{"x":0,"y":0,"z":0,"w":100},"zz":1}""")!;

        Assert.Null(message.Pose); // no listener position
        Assert.Equal(["zz"], message.OtherKeys);
        Assert.Null(SlData.Parse("{\"x\":\"" + new string('a', SlData.MaxMessageLength) + "\"}"));
    }
}

/// <summary>The tether rules: how far a viewer's own report may be from the simulator's position (no media).</summary>
public sealed class EffectivePoseTests
{
    private static readonly SpatialOptions Options = new(); // speaker tether 10 m, listener tether 50 m
    private static readonly Quat East = new(0, 0, 0, 1);
    private static readonly Quat West = new(0, 0, 1, 0);
    private static readonly Vec3 Simulator = new(1000, 1000, 20);

    private static ParticipantSnapshot Avatar(ClientPoseReport? report, Vec3? position = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "avatar", null, position ?? Simulator, East, false, 1, null, DateTimeOffset.UtcNow,
            ParticipantState.Connected, ClientReport: report);

    [Fact]
    public void Without_a_report_the_control_api_pose_is_used()
    {
        var pose = EffectivePose.Of(Avatar(null), Options);

        Assert.Equal(new EffectivePose(Simulator, Simulator, East), pose);
    }

    [Fact]
    public void The_viewers_own_positions_are_used_within_the_tethers()
    {
        var report = new ClientPoseReport(new Vec3(1003, 1000, 21), East, new Vec3(1000, 1040, 25), West);

        var pose = EffectivePose.Of(Avatar(report), Options);

        Assert.Equal(new EffectivePose(new Vec3(1003, 1000, 21), new Vec3(1000, 1040, 25), West), pose);
    }

    [Fact]
    public void A_speaker_too_far_from_the_simulators_position_is_heard_from_the_simulators()
    {
        var report = new ClientPoseReport(new Vec3(1200, 1000, 20), East, new Vec3(1200, 1000, 20), East);

        var pose = EffectivePose.Of(Avatar(report), Options);

        Assert.Equal(Simulator, pose.Speaker);
        Assert.Equal(new Vec3(1050, 1000, 20), pose.Listener); // the camera is pulled back to 50 m
    }

    [Fact]
    public void A_report_without_a_simulator_position_is_not_trusted()
    {
        var report = new ClientPoseReport(new Vec3(5, 5, 5), East, new Vec3(5, 5, 5), West);
        var noPosition = Avatar(report) with { Position = null };

        var pose = EffectivePose.Of(noPosition, Options);

        Assert.Null(pose.Speaker);
        Assert.Null(pose.Listener);
    }
}

/// <summary>Tethered rooms take the viewer's reports; the simulator sends everyone's positions in one batch (no media).</summary>
public sealed class TetheredRoomTests : IDisposable
{
    private const string Pose =
        """{"sp":{"x":300,"y":0,"z":0},"sh":{"x":0,"y":0,"z":0,"w":100},"lp":{"x":0,"y":400,"z":0},"lh":{"x":0,"y":0,"z":100,"w":0}}""";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static async Task<RoomSnapshot> CreateRoom(HttpClient api, object body) =>
        await (await api.PostAsJsonAsync("/api/rooms", body)).Read<RoomSnapshot>();

    private static async Task<JoinResponse> Join(HttpClient api, Guid roomId, string externalId) =>
        await (await api.PostAsJsonAsync($"/api/rooms/{roomId}/participants",
            new { displayName = externalId, externalId, position = new Vec3(0, 0, 0) })).Read<JoinResponse>();

    [Fact]
    public async Task Only_tethered_rooms_take_the_viewers_pose()
    {
        using var server = new ServerFactory(_dir.File("server.db"));
        var api = server.CreateAuthedClient();
        var rooms = server.Services.GetRequiredService<RoomManager>();
        var tethered = await CreateRoom(api, new { name = "region", spatial = true, clientPose = "tethered" });
        var turnOnly = await CreateRoom(api, new { name = "web", spatial = true });
        var inTethered = await Join(api, tethered.Id, "a");
        var inTurnOnly = await Join(api, turnOnly.Id, "b");

        rooms.ReceiveClientData(tethered.Id, inTethered.ParticipantId, Pose);
        rooms.ReceiveClientData(turnOnly.Id, inTurnOnly.ParticipantId, Pose);

        Assert.Equal(ClientPose.Tethered, tethered.ClientPose);
        var report = rooms.GetParticipant(tethered.Id, inTethered.ParticipantId).ClientReport!;
        Assert.Equal(new Vec3(3, 0, 0), report.SpeakerPosition);
        Assert.Equal(new Vec3(0, 4, 0), report.ListenerPosition);
        Assert.Equal(new Quat(0, 0, 1, 0), report.ListenerOrientation);
        Assert.Null(rooms.GetParticipant(turnOnly.Id, inTurnOnly.ParticipantId).ClientReport);
        rooms.ReceiveClientData(tethered.Id, inTethered.ParticipantId, "{\"j\":{\"p\":true}}"); // logged, harmless
        rooms.ReceiveClientData(tethered.Id, Guid.NewGuid(), Pose); // a participant that has gone: ignored
    }

    [Fact]
    public async Task A_viewers_mute_list_and_volumes_are_kept_per_person()
    {
        using var server = new ServerFactory(_dir.File("server.db"));
        var api = server.CreateAuthedClient();
        var rooms = server.Services.GetRequiredService<RoomManager>();
        var room = await CreateRoom(api, new { name = "region", spatial = true, clientPose = "tethered" });
        var viewer = await Join(api, room.Id, Guid.NewGuid().ToString());
        var other = Guid.NewGuid();
        var upper = other.ToString().ToUpperInvariant(); // viewers write lower case, but any form is accepted

        rooms.ReceiveClientData(room.Id, viewer.ParticipantId, "{\"m\":{\"" + upper + "\":true},\"ug\":{\"" + upper + "\":220}}");
        var set = rooms.GetParticipant(room.Id, viewer.ParticipantId).Peers![other.ToString()];
        rooms.ReceiveClientData(room.Id, viewer.ParticipantId, "{\"m\":{\"" + other + "\":false},\"ug\":{\"" + other + "\":100}}");
        rooms.ReceiveClientData(room.Id, viewer.ParticipantId, "{\"m\":{\"not-a-uuid\":true},\"ug\":{\"" + Guid.NewGuid() + "\":5000}}");

        Assert.Equal(new PeerSetting(true, 2.2), set);
        var peers = rooms.GetParticipant(room.Id, viewer.ParticipantId).Peers!;
        Assert.False(peers.ContainsKey(other.ToString())); // back to normal: forgotten
        Assert.Equal(Validation.MaxGain, Assert.Single(peers).Value.Gain); // capped; the bad id ignored
    }

    [Fact]
    public async Task The_simulator_sets_many_poses_at_once_by_external_id()
    {
        using var server = new ServerFactory(_dir.File("server.db"));
        var api = server.CreateAuthedClient();
        var room = await (await api.PutAsJsonAsync("/api/rooms/by-external/region:r1",
            new { name = "region", spatial = true, clientPose = "tethered" })).Read<RoomSnapshot>();
        var a = await Join(api, room.Id, "avatar-a");
        await Join(api, room.Id, "avatar-b");

        var result = await (await api.PostAsJsonAsync("/api/rooms/by-external/region:r1/poses", new
        {
            poses = new object[]
            {
                new { externalId = "avatar-a", position = new Vec3(10, 20, 30), orientation = new Quat(0, 0, 1, 0) },
                new { participantId = (await api.GetFromJsonAsync<List<ParticipantSnapshot>>($"/api/rooms/{room.Id}/participants", Json.Options))!
                    .Single(p => p.ExternalId == "avatar-b").Id, position = new Vec3(1, 2, 3) },
                new { externalId = "gone", position = new Vec3(0, 0, 0) },
            },
        })).Read<PoseBatchResult>();

        Assert.Equal(2, result.Updated);
        Assert.Equal(["gone"], result.Unknown);
        var updated = await api.GetFromJsonAsync<ParticipantSnapshot>($"/api/rooms/{room.Id}/participants/{a.ParticipantId}", Json.Options);
        Assert.Equal(new Vec3(10, 20, 30), updated!.Position);
        Assert.Equal(new Quat(0, 0, 1, 0), updated.Orientation);
    }

    [Fact]
    public async Task Pose_batches_are_validated()
    {
        using var server = new ServerFactory(_dir.File("server.db"));
        var api = server.CreateAuthedClient();
        var room = await CreateRoom(api, new { name = "region", spatial = true, clientPose = "tethered" });

        var noId = await api.PostAsJsonAsync($"/api/rooms/{room.Id}/poses", new { poses = new[] { new { position = new Vec3(0, 0, 0) } } });
        var badQuat = await api.PostAsJsonAsync($"/api/rooms/{room.Id}/poses",
            new { poses = new[] { new { externalId = "x", orientation = new Quat(0, 0, 0, 0) } } });
        var tooMany = await api.PostAsJsonAsync($"/api/rooms/{room.Id}/poses",
            new { poses = Enumerable.Range(0, RoomManager.MaxPoseBatch + 1).Select(i => new { externalId = $"a{i}" }).ToList() });

        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badQuat.StatusCode);
        Assert.False(tooMany.IsSuccessStatusCode);
    }
}
