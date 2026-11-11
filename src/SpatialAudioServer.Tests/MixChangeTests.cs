using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Media;
using SpatialAudioServer.Signalling;

namespace SpatialAudioServer.Tests;

/// <summary>Participants joining, leaving and changing while the room is playing.</summary>
[Collection("media")]
public sealed class MixChangeTests : MediaTestBase
{
    private GstRoomMedia Media => Server.Services.GetRequiredService<GstRoomMedia>();

    [Fact]
    public async Task A_late_joiner_hears_everyone_and_is_heard()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient> { await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1) };
        await SettleAndMeasureAsync(clients, Others(clients));

        clients.Add(await JoinAsync(room.Id, 2));

        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));
        Assert.Equal(6, await Media.ChainCountAsync(room.Id));
    }

    [Fact]
    public async Task Removing_a_participant_via_the_api_takes_them_out_of_every_mix()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient>();
        for (var i = 0; i < 3; i++)
        {
            clients.Add(await JoinAsync(room.Id, i));
        }
        await SettleAndMeasureAsync(clients, Others(clients));

        var leaving = clients[2];
        Assert.Equal(HttpStatusCode.NoContent,
            (await Api.DeleteAsync($"/api/rooms/{room.Id}/participants/{leaving.ParticipantId}")).StatusCode);
        clients.Remove(leaving);

        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));
        Assert.Equal(2, await Media.ChainCountAsync(room.Id));
    }

    [Fact]
    public async Task Leaving_via_signalling_removes_the_participant()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient> { await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1), await JoinAsync(room.Id, 2) };
        await SettleAndMeasureAsync(clients, Others(clients));

        await clients[0].LeaveAsync(Signal);
        clients.RemoveAt(0);

        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));
        var details = await Api.GetFromJsonAsync<RoomDetails>($"/api/rooms/{room.Id}", Json.Options);
        Assert.Equal(2, details!.Participants.Count);
    }

    [Fact]
    public async Task A_muted_participant_is_not_heard_until_unmuted()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient> { await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1), await JoinAsync(room.Id, 2) };
        await SettleAndMeasureAsync(clients, Others(clients));
        var muted = clients[0];

        await Api.PatchAsJsonAsync($"/api/rooms/{room.Id}/participants/{muted.ParticipantId}", new { muted = true });
        Func<FakeClient, IEnumerable<double>> withoutMuted = c => Others(clients)(c).Where(t => t != muted.ToneHz);
        AssertHears(await SettleAndMeasureAsync(clients, withoutMuted), withoutMuted);

        await Api.PatchAsJsonAsync($"/api/rooms/{room.Id}/participants/{muted.ParticipantId}", new { muted = false });
        AssertHears(await SettleAndMeasureAsync(clients, Others(clients)), Others(clients));
    }

    [Fact]
    public async Task Deleting_a_room_with_connected_participants_tears_it_down()
    {
        var room = await CreateRoomAsync();
        var clients = new List<FakeClient> { await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1) };
        await SettleAndMeasureAsync(clients, Others(clients));

        var stopwatch = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"/api/rooms/{room.Id}")).StatusCode);

        await Assert.ThrowsAsync<Rooms.NotFoundException>(() => Media.ChainCountAsync(room.Id));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));

        // The server keeps working for other rooms.
        var other = await CreateRoomAsync();
        var fresh = new List<FakeClient> { await JoinAsync(other.Id, 2), await JoinAsync(other.Id, 3) };
        AssertHears(await SettleAndMeasureAsync(fresh, Others(fresh)), Others(fresh));
    }

    [Fact]
    public async Task Churn_leaves_the_room_consistent_and_does_not_leak_threads()
    {
        var room = await CreateRoomAsync();
        var steady = new List<FakeClient> { await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1) };
        await SettleAndMeasureAsync(steady, Others(steady));
        // Warm-up round: the first join/leave creates threads that are then reused (pools, GStreamer task pools).
        var warmup = await JoinAsync(room.Id, 2);
        await SettleAndMeasureAsync([.. steady, warmup], Others([.. steady, warmup]));
        await Api.DeleteAsync($"/api/rooms/{room.Id}/participants/{warmup.ParticipantId}");
        warmup.Dispose();
        await Task.Delay(1000);
        var baseline = ThreadNames();

        for (var round = 0; round < 5; round++)
        {
            var visitors = new List<FakeClient> { await JoinAsync(room.Id, 2 + round % 3), await JoinAsync(room.Id, 5 + round % 3) };
            var everyone = steady.Concat(visitors).ToList();
            await SettleAndMeasureAsync(everyone, Others(everyone));
            Assert.Equal(12, await Media.ChainCountAsync(room.Id));

            foreach (var visitor in visitors)
            {
                await Api.DeleteAsync($"/api/rooms/{room.Id}/participants/{visitor.ParticipantId}");
                visitor.Dispose();
            }
        }

        AssertHears(await SettleAndMeasureAsync(steady, Others(steady)), Others(steady));
        Assert.Equal(2, await Media.ChainCountAsync(room.Id));

        // Native (GStreamer/GLib and our worker/bus) threads must come back near the baseline. GStreamer's task pool
        // and GLib's thread pool keep a few idle workers (which keep their last task's name, e.g. "queue93:src"),
        // so a handful is expected; a per-join leak shows as +10 or more over 10 joins (UPnP did: +20).
        // .NET thread-pool workers are excluded: that pool grows on its own.
        await Task.Delay(1000);
        var after = ThreadNames();
        var grown = after.Where(kv => !kv.Key.StartsWith(".NET", StringComparison.Ordinal))
            .Select(kv => (kv.Key, Delta: kv.Value - baseline.GetValueOrDefault(kv.Key)))
            .Where(x => x.Delta > 0).ToList();
        Assert.True(grown.Sum(x => x.Delta) < 10,
            "Native threads grew over 10 joins and leaves: " + string.Join(", ", grown.Select(x => $"{x.Key} +{x.Delta}")));
    }

    /// <summary>Thread counts by Linux thread name (/proc/self/task/*/comm).</summary>
    private static Dictionary<string, int> ThreadNames() =>
        Directory.GetDirectories("/proc/self/task")
            .Select(d => { try { return File.ReadAllText(Path.Combine(d, "comm")).Trim(); } catch (IOException) { return "?"; } })
            .GroupBy(n => System.Text.RegularExpressions.Regex.Replace(n, @"[0-9a-f]{4,}|\d+", "#"))
            .ToDictionary(g => g.Key, g => g.Count());

    [Fact]
    public async Task Re_adding_the_same_avatar_while_connected_replaces_the_old_connection()
    {
        var room = await CreateRoomAsync();
        var other = await JoinAsync(room.Id, 0);
        var avatar = Guid.NewGuid().ToString();

        var firstJoin = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants",
            new { displayName = "avatar", externalId = avatar })).Read<JoinResponse>();
        using var first = new FakeClient("first", Tones[1], Tones);
        await first.ConnectAsync(Signal, firstJoin.ParticipantId, firstJoin.JoinToken);
        await SettleAndMeasureAsync([other, first], Others([other, first]));

        var secondJoin = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants",
            new { displayName = "avatar", externalId = avatar })).Read<JoinResponse>();
        Assert.Equal(firstJoin.ParticipantId, secondJoin.ReplacedParticipantId);
        using var second = new FakeClient("second", Tones[2], Tones);
        await second.ConnectAsync(Signal, secondJoin.ParticipantId, secondJoin.JoinToken);

        // The old connection's tone is gone from the mix; the new one is in it.
        AssertHears(await SettleAndMeasureAsync([other, second], Others([other, second])), Others([other, second]));
        Assert.Equal(2, await Media.ChainCountAsync(room.Id));
    }

    [Fact]
    public async Task Signalling_requires_the_right_tokens()
    {
        var room = await CreateRoomAsync();
        var join = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "p" })).Read<JoinResponse>();
        var otherJoin = await (await Api.PostAsJsonAsync($"/api/rooms/{room.Id}/participants", new { displayName = "q" })).Read<JoinResponse>();

        Task<HttpResponseMessage> Send(HttpMethod method, string path, string? bearer, object? body = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            return Signal.SendAsync(request);
        }

        var offer = new OfferRequest("v=0\r\n");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", null, offer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", "nope", offer)).StatusCode);
        // Another participant's token does not work for this one (and is consumed by the attempt).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", otherJoin.JoinToken, offer)).StatusCode);
        // The control-API key is not a signalling credential.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", ServerFactory.ApiKey, offer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/signal/{join.ParticipantId}/ice", join.JoinToken)).StatusCode);

        // A real offer with the right token works once; the join token cannot be reused afterwards.
        using var client = new FakeClient("p", Tones[0], Tones);
        await client.ConnectAsync(Signal, join.ParticipantId, join.JoinToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/signal/{join.ParticipantId}/offer", join.JoinToken, offer)).StatusCode);
    }
}
