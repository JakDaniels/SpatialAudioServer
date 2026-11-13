using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Rooms;
using SpatialAudioServer.Signalling;
using SpatialAudioServer.Storage;

namespace SpatialAudioServer.Tests;

/// <summary>Invited guests choose their own display name; names given by the integration stand (no media).</summary>
public sealed class GuestNameRuleTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Only_client_named_participants_may_rename_themselves()
    {
        using var server = new ServerFactory(_dir.File("server.db"));
        var api = server.CreateAuthedClient();
        var rooms = server.Services.GetRequiredService<RoomManager>();
        var room = await (await api.PostAsJsonAsync("/api/rooms", new { name = "r" })).Read<RoomSnapshot>();
        var guest = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "Guest", clientNamed = true }))
            .Read<JoinResponse>();
        var avatar = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "Ann Avatar" }))
            .Read<JoinResponse>();

        var renamed = rooms.RenameSelf(room.Id, guest.ParticipantId, "  Alice\u0007 ");

        Assert.Equal("Alice", renamed.DisplayName); // trimmed, control characters dropped
        Assert.True(renamed.ClientNamed);
        Assert.Throws<ForbiddenException>(() => rooms.RenameSelf(room.Id, avatar.ParticipantId, "Mallory"));
        Assert.Equal("Ann Avatar", rooms.RenameSelf(room.Id, avatar.ParticipantId, "Ann Avatar").DisplayName); // same name: fine
        Assert.Throws<DomainValidationException>(() => rooms.RenameSelf(room.Id, guest.ParticipantId, "   "));
        Assert.Throws<DomainValidationException>(() => rooms.RenameSelf(room.Id, guest.ParticipantId, new string('x', 201)));
        var activity = server.Services.GetRequiredService<ActivityLog>();
        Assert.Contains(activity.RecentEvents(), e => e.Kind == "participant.renamed" && e.Detail == "Guest → Alice");
        Assert.Contains(activity.Sessions(room.Id), s => s.ParticipantId == guest.ParticipantId && s.DisplayName == "Alice");
        // Events carry the participant's latest name, even ones from before the rename.
        var joined = activity.RecentEvents(roomId: room.Id).Single(e => e.Kind == "participant.joined" && e.ParticipantId == guest.ParticipantId);
        Assert.Equal("Alice", joined.ParticipantName);
        Assert.Null(activity.RecentEvents(roomId: room.Id).Single(e => e.Kind == "room.created").ParticipantName);
    }

    [Fact]
    public async Task The_config_tells_a_guest_it_may_choose_its_name()
    {
        using var server = new ServerFactory(_dir.File("server.db"));
        var api = server.CreateAuthedClient();
        var room = await (await api.PostAsJsonAsync("/api/rooms", new { name = "r" })).Read<RoomSnapshot>();
        var guest = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "Guest", clientNamed = true }))
            .Read<JoinResponse>();
        var signal = server.CreateClient();
        signal.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", guest.JoinToken);

        var config = await signal.GetFromJsonAsync<ClientConfig>($"/signal/{guest.ParticipantId}/config", Json.Options);

        Assert.True(config!.ClientNamed);
        Assert.Equal("Guest", config.DisplayName);
    }
}

/// <summary>The name travels with the offer through real signalling.</summary>
[Collection("media")]
public sealed class GuestNameSignallingTests() : MediaTestBase(SpatialAudioServer.Media.Engines.MixEngines.Default)
{
    [Fact]
    public async Task A_guest_names_itself_with_its_offer_and_a_refused_name_leaves_the_join_token_usable()
    {
        var room = await CreateRoomAsync();
        var guest = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "Guest", clientNamed = true }))
            .Read<JoinResponse>();
        var avatar = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "Ann Avatar" }))
            .Read<JoinResponse>();

        using var alice = new FakeClient("alice", Tones[0], Tones);
        await alice.ConnectAsync(Signal, guest.ParticipantId, guest.JoinToken, displayName: "Alice");
        Assert.Equal("Alice", (await Api.GetFromJsonAsync<ParticipantSnapshot>(
            $"/api/rooms/{room.Id}/participants/{guest.ParticipantId}", Json.Options))!.DisplayName);

        using var mallory = new FakeClient("mallory", Tones[1], Tones);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() =>
            mallory.ConnectAsync(Signal, avatar.ParticipantId, avatar.JoinToken, displayName: "Mallory"));
        Assert.Contains("403", refused.Message);
        using var ann = new FakeClient("ann", Tones[1], Tones);
        await ann.ConnectAsync(Signal, avatar.ParticipantId, avatar.JoinToken); // the join token was not used up
        Assert.Equal("Ann Avatar", (await Api.GetFromJsonAsync<ParticipantSnapshot>(
            $"/api/rooms/{room.Id}/participants/{avatar.ParticipantId}", Json.Options))!.DisplayName);
    }
}
