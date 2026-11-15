using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Signalling;

namespace SpatialAudioServer.Tests;

/// <summary>Rooms say whether the integration shows web users in-world, and it ties a participant to its NPC (no media).</summary>
public sealed class InWorldAvatarTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_rooms_in_world_avatar_setting_is_kept_changed_and_reported_to_clients()
    {
        var path = _dir.File("server.db");
        Guid roomId;
        using (var server = new ServerFactory(path))
        {
            var api = server.CreateAuthedClient();
            var room = await (await api.PutAsJsonAsync("/api/rooms/by-external/region:test", new { spatial = true, inWorldAvatars = "optional" }))
                .Read<RoomSnapshot>();
            Assert.Equal(InWorldAvatars.Optional, room.InWorldAvatars);
            roomId = room.Id;

            var plain = await (await api.PostAsJsonAsync("/api/rooms", new { name = "plain" })).Read<RoomSnapshot>();
            Assert.Equal(InWorldAvatars.Off, plain.InWorldAvatars);

            var changed = await (await api.PatchAsJsonAsync("/api/rooms/by-external/region:test", new { inWorldAvatars = "always" }))
                .Read<RoomSnapshot>();
            Assert.Equal(InWorldAvatars.Always, changed.InWorldAvatars);

            var guest = await (await api.PostAsJsonAsync($"/api/rooms/{roomId}/participants", new { displayName = "Guest" })).Read<JoinResponse>();
            var signal = server.CreateClient();
            signal.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", guest.JoinToken);
            var config = await signal.GetFromJsonAsync<ClientConfig>($"/signal/{guest.ParticipantId}/config", Json.Options);
            Assert.Equal(InWorldAvatars.Always, config!.InWorldAvatars);
        }

        // Stored with the room.
        using (var server = new ServerFactory(path))
        {
            var api = server.CreateAuthedClient();
            var room = await api.GetFromJsonAsync<JsonObject>($"/api/rooms/{roomId}");
            Assert.Equal("always", (string?)room!["inWorldAvatars"]);
        }
    }

    [Fact]
    public async Task The_integration_ties_a_participant_to_its_npc_and_viewers_know_it_by_that()
    {
        using var server = new ServerFactory(_dir.File("server.db"));
        var api = server.CreateAuthedClient();
        var rooms = server.Services.GetRequiredService<RoomManager>();
        var room = await (await api.PostAsJsonAsync("/api/rooms", new { name = "r", spatial = true, inWorldAvatars = "optional" }))
            .Read<RoomSnapshot>();
        var guest = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "Guest" })).Read<JoinResponse>();
        var url = $"/api/rooms/{room.Id}/participants/{guest.ParticipantId}";

        rooms.RequestInWorldAvatar(room.Id, guest.ParticipantId, true);
        var asked = await api.GetFromJsonAsync<ParticipantSnapshot>(url, Json.Options);
        Assert.True(asked!.InWorldAvatar);
        Assert.Equal(guest.ParticipantId.ToString(), asked.ViewerKey);

        var npc = Guid.NewGuid();
        var tied = await (await api.PatchAsJsonAsync(url, new { avatarId = npc })).Read<ParticipantSnapshot>();
        Assert.Equal(npc, tied.AvatarId);
        Assert.Equal(npc.ToString(), tied.ViewerKey);

        var untied = await (await api.PatchAsJsonAsync(url, new { avatarId = Guid.Empty })).Read<ParticipantSnapshot>();
        Assert.Null(untied.AvatarId);
        Assert.Equal(guest.ParticipantId.ToString(), untied.ViewerKey);
    }
}
