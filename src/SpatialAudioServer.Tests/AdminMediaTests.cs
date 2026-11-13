using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SpatialAudioServer.Api;
using SpatialAudioServer.LoadTest;
using SpatialAudioServer.Media;

namespace SpatialAudioServer.Tests;

/// <summary>The admin UI's live data from a real room: levels, connection statistics, the pipeline graph.</summary>
[Collection("media")]
public sealed class AdminMediaTests() : MediaTestBase(SpatialAudioServer.Media.Engines.MixEngines.Default)
{
    private async Task<HttpClient> AdminAsync()
    {
        Server.Services.GetRequiredService<AdminPassword>().Set("admin-test-password");
        var admin = Server.CreateClient();
        (await admin.PostAsJsonAsync("/api/admin/login", new { password = "admin-test-password" })).EnsureSuccessStatusCode();
        return admin;
    }

    [Fact]
    public async Task Live_stats_show_levels_and_connection_statistics()
    {
        var room = await CreateRoomAsync();
        FakeClient[] talkers = [await JoinAsync(room.Id, 0), await JoinAsync(room.Id, 1)];
        var silent = await JoinAsync(room.Id, 2, talking: false);
        await SettleAndMeasureAsync([.. talkers, silent], Others(talkers));
        var admin = await AdminAsync();
        await Task.Delay(1500); // let RTCP reports arrive

        var live = await admin.GetFromJsonAsync<RoomLiveStats>($"/api/admin/rooms/{room.Id}/live", Json.Options);

        Assert.Equal(3, live!.Participants.Count);
        Assert.Equal("matrix-shared", live.Media.Engine);
        foreach (var talker in talkers)
        {
            var p = live.Participants.Single(x => x.Id == talker.ParticipantId);
            Assert.InRange(p.LevelDb!.Value, -30, -15); // a 0.1 sine is −23 dBFS RMS
        }
        var quiet = live.Participants.Single(x => x.Id == silent.ParticipantId);
        Assert.True(quiet.LevelDb is null or < -60, $"silent client level {quiet.LevelDb}");
        Assert.All(live.Participants, p =>
        {
            var c = p.Connection!;
            Assert.True(c.PacketsReceived > 0, "packets from the client");
            Assert.True(c.BytesSent > 0, "bytes to the client");
            Assert.Equal("host → host", c.IcePath);
        });
    }

    [Fact]
    public async Task The_pipeline_graph_downloads_and_renders()
    {
        var room = await CreateRoomAsync();
        var client = await JoinAsync(room.Id, 0);
        await SettleAndMeasureAsync([client], _ => []);
        var admin = await AdminAsync();

        var dot = await admin.GetAsync($"/api/admin/rooms/{room.Id}/graph.dot");
        Assert.Equal(HttpStatusCode.OK, dot.StatusCode);
        var text = await dot.Content.ReadAsStringAsync();
        Assert.StartsWith("digraph", text.TrimStart());
        Assert.Contains("GstWebRTCBin", text);

        var svg = await admin.GetAsync($"/api/admin/rooms/{room.Id}/graph.svg");
        Assert.True(svg.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotImplemented, $"graph.svg: {svg.StatusCode}");
        if (svg.StatusCode == HttpStatusCode.OK)
        {
            Assert.Contains("<svg", await svg.Content.ReadAsStringAsync());
        }
    }
}
