using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.Tests;

/// <summary>A client that vanishes without leaving is removed once its connection has been dead for the timeout.</summary>
[Collection("media")]
public sealed class DeadConnectionTests() : MediaTestBase(
    SpatialAudioServer.Media.Engines.MixEngines.Default,
    new Dictionary<string, string?> { ["Media:DeadConnectionTimeout"] = "00:00:02" })
{
    [Fact]
    public async Task A_vanished_client_is_removed_and_the_others_stay()
    {
        var room = await CreateRoomAsync();
        var clients = new List<LoadTest.FakeClient> { await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1) };
        await SettleAndMeasureAsync(clients, Others(clients));
        var gone = clients[1];

        var clock = Stopwatch.StartNew();
        gone.Dispose(); // stops sending without leaving through the API, like a closed laptop
        var states = new List<string>();
        ParticipantState? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            using var response = await Api.GetAsync($"/api/rooms/{room.Id}/participants/{gone.ParticipantId}");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                break;
            }
            var state = (await response.Read<ParticipantSnapshot>()).State;
            if (state != last)
            {
                states.Add($"{clock.Elapsed.TotalSeconds:F1}s {state}");
                last = state;
            }
            await Task.Delay(250);
        }

        // The server notices ~27 s after the client goes (ICE consent freshness expires: RFC 7675, libnice's 30 s
        // since the last good check), reports Failed, and removes it DeadConnectionTimeout (2 s) later.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(90), $"never removed; states: {string.Join(", ", states)}");
        var remaining = await (await Api.GetAsync($"/api/rooms/{room.Id}/participants")).Read<List<ParticipantSnapshot>>();
        var stay = Assert.Single(remaining);
        Assert.Equal(clients[0].ParticipantId, stay.Id);
        Assert.Equal(ParticipantState.Connected, stay.State);
    }
}

/// <summary>Which states count as dead and for how long, driven directly (no media).</summary>
public sealed class DeadConnectionRuleTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Only_failed_or_disconnected_participants_past_the_timeout_are_removed()
    {
        // Timeout 0 turns the background sweeper off, so the test drives the sweep itself.
        using var server = new ServerFactory(_dir.File("server.db"), new Dictionary<string, string?> { ["Media:DeadConnectionTimeout"] = "0" });
        var api = server.CreateAuthedClient();
        var rooms = server.Services.GetRequiredService<RoomManager>();
        var room = await (await api.PostAsJsonAsync("/api/rooms", new { name = "sweep" })).Read<RoomSnapshot>();
        async Task<Guid> Add(ParticipantState? state)
        {
            var join = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = $"p-{state}" })).Read<JoinResponse>();
            if (state is { } s)
            {
                rooms.SetParticipantState(room.Id, join.ParticipantId, s);
            }
            return join.ParticipantId;
        }
        var waiting = await Add(null);
        var connecting = await Add(ParticipantState.Connecting);
        var connected = await Add(ParticipantState.Connected);
        var failed = await Add(ParticipantState.Failed);
        var disconnected = await Add(ParticipantState.Disconnected);
        var recovered = await Add(ParticipantState.Failed);
        rooms.SetParticipantState(room.Id, recovered, ParticipantState.Connecting); // the client re-offered in time

        Assert.Equal(0, rooms.DropDeadConnections(TimeSpan.FromHours(1))); // not dead for long enough yet
        Assert.Equal(2, rooms.DropDeadConnections(TimeSpan.Zero));

        var left = rooms.ListParticipants(room.Id).Select(p => p.Id).ToHashSet();
        Assert.Equal(new HashSet<Guid> { waiting, connecting, connected, recovered }, left);
        Assert.DoesNotContain(failed, left);
        Assert.DoesNotContain(disconnected, left);
    }

    [Fact]
    public async Task A_participant_whose_join_token_expired_unused_is_removed()
    {
        using var server = new ServerFactory(_dir.File("server.db"), new Dictionary<string, string?>
        {
            ["Media:DeadConnectionTimeout"] = "0",
            ["Auth:JoinTokenLifetime"] = "00:00:01",
        });
        var api = server.CreateAuthedClient();
        var signal = server.CreateClient();
        var rooms = server.Services.GetRequiredService<RoomManager>();
        var room = await (await api.PostAsJsonAsync("/api/rooms", new { name = "sweep" })).Read<RoomSnapshot>();
        var neverCame = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "away" })).Read<JoinResponse>();
        var offered = await (await api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "here" })).Read<JoinResponse>();
        // The offer redeems the token (media is off here, so the answer itself fails afterwards).
        using (var offer = new HttpRequestMessage(HttpMethod.Post, $"/signal/{offered.ParticipantId}/offer")
        {
            Content = JsonContent.Create(new { sdp = "v=0" }),
        })
        {
            offer.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", offered.JoinToken);
            await signal.SendAsync(offer);
        }

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        rooms.DropExpiredJoins(); // the background sweep (every 5 s) may also get there first

        Assert.Equal(offered.ParticipantId, Assert.Single(rooms.ListParticipants(room.Id)).Id);
        var events = server.Services.GetRequiredService<SpatialAudioServer.Storage.ActivityLog>().RecentEvents(10, room.Id, null);
        Assert.Contains(events, e => e.Kind == "participant.left" && e.ParticipantId == neverCame.ParticipantId && e.Detail == "join-expired");
    }
}
