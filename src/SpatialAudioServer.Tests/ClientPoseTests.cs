using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>What participants may change about their own pose (no media: the rules, not the sound).</summary>
public sealed class ClientPoseTests : IDisposable
{
    private static readonly Quat FacingWest = new(0, 0, 1, 0);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ServerFactory Server() => new(_dir.File("server.db"));

    private static async Task<RoomSnapshot> CreateRoom(HttpClient api, object body) =>
        await (await api.PostAsJsonAsync("/api/rooms", body)).Read<RoomSnapshot>();

    private static async Task<Guid> Join(HttpClient api, Guid roomId) =>
        (await (await api.PostAsJsonAsync($"/api/rooms/{roomId}/participants", new { displayName = "p" })).Read<JoinResponse>()).ParticipantId;

    [Fact]
    public async Task Spatial_rooms_let_participants_turn_by_default_and_the_setting_persists()
    {
        RoomSnapshot spatial, plain, locked;
        using (var server = Server())
        {
            var api = server.CreateAuthedClient();
            spatial = await CreateRoom(api, new { name = "region", spatial = true });
            plain = await CreateRoom(api, new { name = "meeting" });
            locked = await CreateRoom(api, new { name = "strict region", spatial = true, clientPose = "none" });
            Assert.Equal(ClientPose.Orientation, spatial.ClientPose);
            Assert.Equal(ClientPose.None, plain.ClientPose);
            Assert.Equal(ClientPose.None, locked.ClientPose);
            Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/rooms", new { name = "x", clientPose = "fly" })).StatusCode);
        }
        using var restarted = Server();
        var after = restarted.Services.GetRequiredService<RoomManager>();
        Assert.Equal(ClientPose.Orientation, after.GetRoom(spatial.Id).Room.ClientPose);
        Assert.Equal(ClientPose.None, after.GetRoom(locked.Id).Room.ClientPose);
    }

    [Fact]
    public async Task Participants_may_turn_only_where_the_room_allows_and_never_move()
    {
        using var server = Server();
        var api = server.CreateAuthedClient();
        var rooms = server.Services.GetRequiredService<RoomManager>();
        var spatial = await CreateRoom(api, new { name = "region", spatial = true });
        var plain = await CreateRoom(api, new { name = "meeting" });
        var inSpatial = await Join(api, spatial.Id);
        var inPlain = await Join(api, plain.Id);

        var turned = rooms.UpdateOwnPose(spatial.Id, inSpatial, new SelfPoseCommand(FacingWest));

        Assert.Equal(FacingWest, turned.ListenerOrientation);
        Assert.Null(turned.Orientation); // the server-set orientation is untouched
        Assert.Throws<ForbiddenException>(() => rooms.UpdateOwnPose(plain.Id, inPlain, new SelfPoseCommand(FacingWest)));
        Thread.Sleep(RoomManager.SelfPoseInterval);
        Assert.Throws<ForbiddenException>(() =>
            rooms.UpdateOwnPose(spatial.Id, inSpatial, new SelfPoseCommand(null, new Vec3(30, 30, 0))));
        Assert.Throws<DomainValidationException>(() => rooms.UpdateOwnPose(spatial.Id, inSpatial, new SelfPoseCommand(new Quat(0, 0, 0, 0))));
    }

    [Fact]
    public async Task Own_pose_updates_are_rate_limited()
    {
        using var server = Server();
        var api = server.CreateAuthedClient();
        var rooms = server.Services.GetRequiredService<RoomManager>();
        var room = await CreateRoom(api, new { name = "region", spatial = true });
        var participant = await Join(api, room.Id);

        rooms.UpdateOwnPose(room.Id, participant, new SelfPoseCommand(FacingWest));

        Assert.Throws<RateLimitedException>(() => rooms.UpdateOwnPose(room.Id, participant, new SelfPoseCommand(FacingWest)));
        Thread.Sleep(RoomManager.SelfPoseInterval);
        rooms.UpdateOwnPose(room.Id, participant, new SelfPoseCommand(FacingWest));
    }

    [Fact]
    public async Task The_control_api_may_set_listener_poses()
    {
        using var server = Server();
        var api = server.CreateAuthedClient();
        var room = await CreateRoom(api, new { name = "meeting" }); // clientPose none: still fine for the control API
        var participant = await Join(api, room.Id);

        var updated = await (await api.PatchAsJsonAsync($"/api/rooms/{room.Id}/participants/{participant}",
            new { listenerPosition = new Vec3(1, 2, 3), listenerOrientation = FacingWest })).Read<ParticipantSnapshot>();

        Assert.Equal(new Vec3(1, 2, 3), updated.ListenerPosition);
        Assert.Equal(FacingWest, updated.ListenerOrientation);
    }

    [Fact]
    public async Task The_pose_endpoint_needs_the_session_token()
    {
        using var server = Server();
        var api = server.CreateAuthedClient();
        var room = await CreateRoom(api, new { name = "region", spatial = true });
        var join = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "p" })).Read<JoinResponse>();
        var signal = server.CreateClient();

        var anonymous = await signal.PatchAsJsonAsync($"/signal/{join.ParticipantId}/pose", new { listenerOrientation = FacingWest });
        using var withJoinToken = new HttpRequestMessage(HttpMethod.Patch, $"/signal/{join.ParticipantId}/pose")
        {
            Content = JsonContent.Create(new { listenerOrientation = FacingWest }),
        };
        withJoinToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", join.JoinToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await signal.SendAsync(withJoinToken)).StatusCode); // join tokens are for the offer
    }
}
